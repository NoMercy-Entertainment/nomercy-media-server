// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Services.Music;
using NoMercy.Authorization;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Http;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Hubs;

public partial class MusicHub
{
    public async Task StartPlaybackCommand(string? type, Guid? listId, Guid? trackId)
    {
        // Epoch-ms marks (not just deltas) so a live device measurement can line
        // this line up directly against the client's own epoch-stamped tap time —
        // see the matching marks in PlaybackCommand and
        // MusicPlaybackService.DebouncedUpdatePlaybackState.
        long entryMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        // Guard: clients occasionally send null for one of these (e.g. an
        // artist with no tracks → trackId is undefined on the client side
        // → null on the wire). Without this check the SignalR-generated
        // invocation thunk NREs while unboxing null into the value-type
        // parameter, before the method body even runs.
        if (string.IsNullOrEmpty(type) || listId is null || trackId is null)
        {
            _logger.LogWarning(
                "{Name}: [MusicHub.StartPlaybackCommand] ignored — null arg (type='{Null}', listId={Null2}, trackId={Null3})",
                user.Name,
                type ?? "<null>",
                listId?.ToString() ?? "<null>",
                trackId?.ToString() ?? "<null>"
            );
            return;
        }

        SemaphoreSlim userLock = GetUserLock(user.Id);
        await userLock.WaitAsync();
        long lockAcquiredMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // SignalR dispatches invocations on one connection serially by default
        // (MaximumParallelInvocationsPerClient=1, left untouched deliberately —
        // raising it globally would let ReportPositionForItemCommand/PlaybackCommand
        // run CONCURRENTLY with this method on the same connection, and every mutation
        // in this hub assumes single-threaded access per user via GetUserLock; that
        // would need a full thread-safety audit of MusicHub, not a one-line config
        // flip). That means a slow GetPlaylist (contended DB, cold cache, a large
        // artist) starves this SAME connection's queued position reports for its
        // entire duration, while MusicPlaybackService's 100ms watchdog keeps ticking
        // independently and would otherwise see no heartbeat and kill the session
        // out from under a client that is still very much alive and simply queued.
        // BeginPlaybackStart/EndPlaybackStart bracket that window so the watchdog
        // never treats it as staleness; a genuinely dead device is still caught on
        // the very next cadence once the flag clears (see MusicPlaybackService).
        _musicPlaybackService.BeginPlaybackStart(user.Id);
        try
        {
            string country = GetCountryFromContext();

            System.Diagnostics.Stopwatch playlistStopwatch =
                System.Diagnostics.Stopwatch.StartNew();
            (PlaylistTrackDto item, List<PlaylistTrackDto> playlist) =
                await _musicPlaylistManager.GetPlaylist(
                    user.Id,
                    type,
                    listId.Value,
                    trackId.Value,
                    country
                );
            playlistStopwatch.Stop();
            long playlistFetchedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _logger.Log(
                playlistStopwatch.ElapsedMilliseconds > 1000 ? LogLevel.Warning : LogLevel.Debug,
                "[MusicHub.StartPlaybackCommand] GetPlaylist({Type}) took {ElapsedMilliseconds}ms ({Count} tracks)",
                type,
                playlistStopwatch.ElapsedMilliseconds,
                playlist.Count
            );

            await HandlePlaybackState(user, type, listId.Value, item, playlist);

            // Round-trip proof: entry -> lock -> playlist fetch -> HandlePlaybackState
            // (which awaits MusicPlaybackService.UpdatePlaybackState internally, so
            // broadcastMs is when the relay's SendTo/Task.WhenAll across every
            // connected device for this user actually completed).
            long broadcastMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _logger.LogInformation(
                "{Name}: [MusicHub.StartPlaybackCommand] type={Type} entryMs={EntryMs} lockAcquiredMs={LockAcquiredMs} (+{LockWaitMs}ms) playlistFetchedMs={PlaylistFetchedMs} (+{PlaylistMs}ms) broadcastMs={BroadcastMs} (+{BroadcastDeltaMs}ms) totalMs={TotalMs}ms playlist={PlaylistCount}",
                user.Name,
                type,
                entryMs,
                lockAcquiredMs,
                lockAcquiredMs - entryMs,
                playlistFetchedMs,
                playlistFetchedMs - lockAcquiredMs,
                broadcastMs,
                broadcastMs - playlistFetchedMs,
                broadcastMs - entryMs,
                playlist.Count
            );
        }
        catch (ArgumentException ex)
        {
            _logger.LogInformation("Invalid playlist type: {Message}", ex.Message);

            ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? client2);
            Ulid deviceId2 = client2?.Id ?? Ulid.Empty;
            try
            {
                await ActivityLogger.LogFailureAsync(
                    "failure.playback_start",
                    user.Id,
                    deviceId2,
                    errorCode: ex.GetType().Name,
                    message: ex.Message
                );
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(
                    "Failed to log failure.playback_start: {Message}",
                    logEx.Message
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Error in StartPlaybackCommand: {Message}", ex.Message);

            ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? client2);
            Ulid deviceId2 = client2?.Id ?? Ulid.Empty;
            try
            {
                await ActivityLogger.LogFailureAsync(
                    "failure.playback_start",
                    user.Id,
                    deviceId2,
                    errorCode: ex.GetType().Name,
                    message: ex.Message
                );
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(
                    "Failed to log failure.playback_start: {Message}",
                    logEx.Message
                );
            }
        }
        finally
        {
            _musicPlaybackService.EndPlaybackStart(user.Id);
            userLock.Release();
        }
    }

    private async Task HandlePlaybackState(
        User user,
        string type,
        Guid listId,
        PlaylistTrackDto item,
        List<PlaylistTrackDto> playlist
    )
    {
        MusicPlayerState? playerState = _musicPlayerStateManager.GetState(user.Id);

        // Special handling for type="track" - only works with existing player state
        if (type.ToLower().Trim() == "track")
        {
            if (playerState?.CurrentItem is null)
            {
                // No active player state, cannot reorder - log and return
                _logger.LogInformation("Cannot play track: No active playlist");
                return;
            }
            await HandleTrackReorder(user, playerState, item);
            return;
        }

        // Normal playlist handling
        if (playerState?.CurrentItem is null || playerState.Playlist.Count == 0)
            await HandleNewPlayerState(user, type, listId, item, playlist);
        else if (IsSamePlaylistAndTrack(playerState, type, listId, item.Id))
            await HandleExistingPlaylistState(user, playerState);
        else if (IsSamePlaylist(playerState, type, listId))
            await HandleTrackReorder(user, playerState, item);
        else
            await HandlePlaylistChange(user, playerState, type, listId, item, playlist);
    }

    private async Task HandleNewPlayerState(
        User user,
        string type,
        Guid listId,
        PlaylistTrackDto item,
        List<PlaylistTrackDto> playlist
    )
    {
        // GetOrPromoteActiveDevice respects an existing connected active
        // device, falling back to the caller only when there isn't one.
        // This prevents a passive sender (phone tapping a playlist after
        // a stop) from being promoted to active just because the previous
        // state had no CurrentItem — the active flag stays on the TV.
        Device device = GetOrPromoteActiveDevice(user);
        MusicPlayerState musicPlayerState = MusicPlayerStateFactory.Create(
            device,
            item,
            playlist,
            type,
            listId
        );
        musicPlayerState.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);

        _musicPlaybackService.RemoveTimer(user.Id);
        _musicPlayerStateManager.UpdateState(user.Id, musicPlayerState);
        _musicPlaybackService.StartPlaybackTimer(user);
        await _musicPlaybackService.UpdatePlaybackState(user, musicPlayerState);
        await _musicPlaybackService.PublishStartedEventAsync(user.Id, musicPlayerState);

        try
        {
            await ActivityLogger.LogPlaybackAsync(
                "playback.started",
                user.Id,
                device.Id,
                Ulid.Empty,
                new
                {
                    media_type = "audio",
                    track_id = item.Id,
                    title = item.Name,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to log playback.started: {Message}", ex.Message);
        }
    }

    private static bool IsSamePlaylist(MusicPlayerState state, string type, Guid listId)
    {
        return state.CurrentItem is not null
            && state
                .CurrentList.ToString()
                .Contains($"{MusicPlayerStateFactory.ToRouteSegment(type)}/{listId}");
    }

    private static bool IsSamePlaylistAndTrack(
        MusicPlayerState state,
        string type,
        Guid listId,
        Guid itemId
    )
    {
        return IsSamePlaylist(state, type, listId) && state.CurrentItem?.Id == itemId;
    }

    private async Task HandleExistingPlaylistState(User user, MusicPlayerState state)
    {
        // Promotes the caller to active only when there is no active device recorded
        // (e.g. resuming right after a graceful release — ChangeDeviceCommand("") or a
        // disconnect) or when the caller already IS active; a passive device toggling
        // play/pause while someone else owns the session must never steal it. Without
        // this, resuming the exact same track/list from a new device after a release
        // left the session with DeviceId permanently null — no device recognized as
        // active, so the liveness watchdog could never refresh or ever expire it.
        UpdateDeviceInfo(state);

        state.PlayState = !state.PlayState;
        UpdateActionsDisallows(state);
        _musicPlaybackService.StartPlaybackTimer(user);
        await _musicPlaybackService.UpdatePlaybackState(user, state);
        if (state.PlayState)
        {
            await _musicPlaybackService.PublishStartedEventAsync(user.Id, state);
        }
    }

    private async Task HandleTrackReorder(User user, MusicPlayerState state, PlaylistTrackDto item)
    {
        // Stop the old timer before modifying state to prevent race conditions
        _musicPlaybackService.RemoveTimer(user.Id);

        // See the identical call in HandleExistingPlaylistState: promotes the caller to
        // active only when nobody currently owns the session, so resuming/reordering
        // from a new device right after a graceful release actually claims it.
        UpdateDeviceInfo(state);

        // Check if it's the current item
        if (state.CurrentItem?.Id == item.Id)
        {
            // Already playing this track, just restart it
            state.SetPosition(0);
            state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
            state.PlayState = true;
            UpdateActionsDisallows(state);
            _musicPlaybackService.StartPlaybackTimer(user);
            await _musicPlaybackService.UpdatePlaybackState(user, state);
            return;
        }

        // Find the track in the current playlist
        int playlistIndex = state.Playlist.FindIndex(t => t.Id == item.Id);

        if (playlistIndex != -1)
        {
            // Track is in the upcoming playlist
            // Add current item to backlog
            if (state.CurrentItem != null)
            {
                state.Backlog.Add(state.CurrentItem);
            }

            // Add all tracks BEFORE the selected one to backlog (they're being skipped over)
            for (int i = 0; i < playlistIndex; i++)
            {
                state.Backlog.Add(state.Playlist[i]);
            }

            // Remove everything up to and including the selected track
            state.Playlist.RemoveRange(0, playlistIndex + 1);

            // Set the selected track as current
            // The remaining playlist continues naturally from here
            state.CurrentItem = item;
            state.SetPosition(0);
            state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
            state.PlayState = true;
        }
        else
        {
            // Check if track is in backlog (going backwards)
            int backlogIndex = state.Backlog.FindIndex(t => t.Id == item.Id);

            if (backlogIndex != -1)
            {
                // Track is in backlog - going backwards
                // Remove it from backlog
                state.Backlog.RemoveAt(backlogIndex);

                // Add current item to backlog
                if (state.CurrentItem != null)
                {
                    state.Backlog.Add(state.CurrentItem);
                }

                // Set the selected track as current
                state.CurrentItem = item;
                state.SetPosition(0);
                state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
                state.PlayState = true;
            }
            else
            {
                // Track not found in current queue at all
                _logger.LogInformation("Track {Id} not found in current queue", item.Id);
                return;
            }
        }

        UpdateActionsDisallows(state);
        _musicPlaybackService.StartPlaybackTimer(user);
        await _musicPlaybackService.UpdatePlaybackState(user, state);
    }

    private async Task HandlePlaylistChange(
        User user,
        MusicPlayerState state,
        string type,
        Guid listId,
        PlaylistTrackDto item,
        List<PlaylistTrackDto> playlist
    )
    {
        _musicPlaybackService.RemoveTimer(user.Id);

        UpdateDeviceInfo(state);
        UpdatePlaylistInfo(state, type, listId, item, playlist);
        UpdateActionsDisallows(state);

        _musicPlaybackService.StartPlaybackTimer(user);
        await _musicPlaybackService.UpdatePlaybackState(user, state);
        await _musicPlaybackService.PublishStartedEventAsync(user.Id, state);

        // Logging only — record who triggered the playlist change without
        // promoting them to active. The active flag is governed by
        // UpdateDeviceInfo, which respects an existing active device.
        Device device = GetCallerDevice(user);
        try
        {
            await ActivityLogger.LogPlaybackAsync(
                "playback.started",
                user.Id,
                device.Id,
                Ulid.Empty,
                new
                {
                    media_type = "audio",
                    track_id = item.Id,
                    title = item.Name,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to log playback.started: {Message}", ex.Message);
        }
    }

    private void UpdatePlaylistInfo(
        MusicPlayerState state,
        string type,
        Guid listId,
        PlaylistTrackDto item,
        List<PlaylistTrackDto> playlist
    )
    {
        (List<PlaylistTrackDto> before, List<PlaylistTrackDto> after) =
            _musicPlaylistManager.SplitPlaylist(playlist, item.Id);
        List<PlaylistTrackDto> sortedPlaylist = [];
        sortedPlaylist.AddRange(after);
        sortedPlaylist.AddRange(before);

        state.CurrentItem = item;
        state.PlayState = true;
        state.Playlist = sortedPlaylist;
        state.CurrentList = new(
            $"/music/{MusicPlayerStateFactory.ToRouteSegment(type)}/{listId}",
            UriKind.Relative
        );
        state.Backlog.Add(item);
        state.SetPosition(0);
        state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
    }

    private void UpdateActionsDisallows(MusicPlayerState state)
    {
        state.Actions = new()
        {
            Disallows = new()
            {
                // Can't pause if already paused
                Pausing = !state.PlayState,
                // Can't resume if already playing
                Resuming = state.PlayState,
                // Can't go to previous if backlog is empty (no tracks to go back to)
                Previous = state.Backlog.Count <= 0,
                // Can't go to next if playlist is empty and repeat is off
                Next = state.Playlist.Count <= 0 && state.Repeat == "off",
                // Basic actions that are always allowed during playback
                Seeking = false,
                Stopping = false,
                Muting = false,
                TogglingShuffle = false,
                TogglingRepeatContext = false,
                TogglingRepeatTrack = false,
            },
        };
    }

    public MusicPlayerState? GetStateCommand()
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return null;

        _musicPlayerStateManager.TryGetValue(user.Id, out MusicPlayerState? playerState);
        if (playerState is null)
            return null;

        // A state read from the active device is free proof of life (no extra I/O)
        // and closes the gap for a client that polls state rather than pushing
        // position reports on its own cadence.
        if (ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? caller))
            MusicPlaybackService.TryRefreshHeartbeat(playerState, caller.DeviceId);

        // A direct read is its own clock-sync emit — without a fresh stamp here a
        // client polling state (rather than receiving a push) would derive elapsed
        // time against a ServerTimeMs left over from whenever the last broadcast
        // happened to fire.
        playerState.ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return playerState;
    }

    public async Task PlaybackCommand(string? command, object? data = null)
    {
        // See the matching epoch-ms marks in StartPlaybackCommand and
        // MusicPlaybackService.DebouncedUpdatePlaybackState — same convention,
        // so a live device measurement can subtract this against the client's
        // own epoch-stamped tap time and see exactly where the round trip goes.
        long entryMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        if (string.IsNullOrEmpty(command))
        {
            _logger.LogWarning(
                "{Name}: [MusicHub.PlaybackCommand] ignored — command was null/empty",
                user.Name
            );
            return;
        }

        SemaphoreSlim userLock = GetUserLock(user.Id);
        await userLock.WaitAsync();
        long lockAcquiredMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            if (!_musicPlayerStateManager.TryGetValue(user.Id, out MusicPlayerState? state))
            {
                await _musicPlaybackService.UpdatePlaybackState(user, null);
                return;
            }

            _commandHandler.HandleCommand(user, command, data, state);

            if (state.DeviceId == null)
                if (ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? device))
                {
                    state.DeviceId = device.DeviceId;
                    state.VolumePercentage = device.VolumePercent ?? Device.DefaultVolumePercent;
                }

            // A command from the active device is proof of life just as much as a
            // position report — seek/mute/shuffle/repeat never call StartPlaybackTimer
            // (unlike play/next/previous), so without this an active device that is
            // only being interacted with, never idly reporting position, could look
            // stale to MusicPlaybackService's sweep despite being very much alive.
            if (ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? caller))
                MusicPlaybackService.TryRefreshHeartbeat(state, caller.DeviceId);

            UpdateActionsDisallows(state);

            long handlerDoneMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            bool isSkipCommand =
                command.Equals("next", StringComparison.OrdinalIgnoreCase)
                || command.Equals("previous", StringComparison.OrdinalIgnoreCase);

            // next/previous go through the 150ms debounce — broadcastMs below only
            // marks when the debounce Timer was SCHEDULED, not when the relay
            // actually left; MusicPlaybackService.DebouncedUpdatePlaybackState logs
            // the real fire-and-send timing separately once the timer elapses.
            if (isSkipCommand)
                _musicPlaybackService.DebouncedUpdatePlaybackState(user, state);
            else
                await _musicPlaybackService.UpdatePlaybackState(user, state);

            long broadcastMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _logger.LogInformation(
                "{Name}: [MusicHub.PlaybackCommand] cmd={Command} entryMs={EntryMs} lockAcquiredMs={LockAcquiredMs} (+{LockWaitMs}ms) handlerDoneMs={HandlerDoneMs} (+{HandlerMs}ms) broadcastMs={BroadcastMs} (+{BroadcastDeltaMs}ms) totalMs={TotalMs}ms debounced={Debounced} playlist={PlaylistCount} backlog={BacklogCount}",
                user.Name,
                command,
                entryMs,
                lockAcquiredMs,
                lockAcquiredMs - entryMs,
                handlerDoneMs,
                handlerDoneMs - lockAcquiredMs,
                broadcastMs,
                broadcastMs - handlerDoneMs,
                broadcastMs - entryMs,
                isSkipCommand,
                state.Playlist.Count,
                state.Backlog.Count
            );
        }
        finally
        {
            userLock.Release();
        }
    }

    // Untagged: the server has no track id to compare a report against, so it
    // cannot reject a report that has gone stale across a track change — it can
    // only be trusted as-is. Kept exactly as-is (signature and behavior) for old
    // clients; new clients should prefer CurrentTimeForItemCommand /
    // ReportPositionForItemCommand below, which close that gap.
    public async Task CurrentTimeCommand(int? time)
    {
        if (time is null)
            return;
        await ReportPositionCommand(time.Value * 1000);
    }

    // See the untagged-vs-tagged note on CurrentTimeCommand above.
    public Task ReportPositionCommand(int? positionMs) => ReportPositionCoreAsync(positionMs, null);

    private async Task ReportPositionCoreAsync(int? positionMs, long? capturedAtMs)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        if (positionMs is null)
            return;

        // See MusicPlaybackService.WithStateLockAsync's own doc: this used to
        // mutate MusicPlayerState and broadcast it with no lock at all, the one
        // hub call in this file that didn't, racing StartPlaybackTimer's own
        // tick for the same object.
        await _musicPlaybackService.WithStateLockAsync(
            user.Id,
            async () =>
            {
                if (!_musicPlayerStateManager.TryGetValue(user.Id, out MusicPlayerState? playerState))
                {
                    await _musicPlaybackService.UpdatePlaybackState(user, playerState);
                    return;
                }

                ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? caller);

                // Liveness first, ahead of the ignore-window gate below: a report from the
                // ACTIVE device that is about to be dropped for landing inside the ignore
                // window still proves the device is alive right now. Only the device the
                // server considers active may prove the session is genuinely still playing
                // somewhere or move the authoritative position — a stray/passive report
                // must never mask a truly-dead active device from MusicPlaybackService's
                // staleness sweep, and must never snap everyone else's playback back to a
                // passive mirror's own (possibly paused, torn down, or drifted) position. A
                // passive report is a complete no-op for both liveness and position.
                if (!MusicPlaybackService.TryRefreshHeartbeat(playerState, caller?.DeviceId))
                    return;

                if (DateTime.UtcNow < playerState.IgnoreCurrentTimeUntil)
                    return;

                playerState.SetPosition(positionMs.Value, capturedAtMs ?? 0);

                await _musicPlaybackService.UpdatePlaybackState(user, playerState);
            }
        );
    }

    /// <summary>
    /// <see cref="ReportPositionCommand"/> plus the server-clock instant the reporting device
    /// actually read that position.
    ///
    /// Without it the position is stamped when the report lands, so the stored position is
    /// already older than it claims by however long the report took to arrive. Every passive
    /// device anchors its interpolation to that instant, so all of them run that far behind
    /// the device making the sound — which is what put lyrics out of step.
    ///
    /// A separate method rather than an extra argument on the existing one: a hub method's
    /// arity is part of its contract, and clients that still call the old one must keep
    /// working exactly as they did.
    /// </summary>
    public Task ReportPositionAtCommand(int? positionMs, long? capturedAtMs) =>
        ReportPositionCoreAsync(positionMs, capturedAtMs);

    /// <summary>
    /// Item-tagged twin of <see cref="CurrentTimeCommand"/> — seconds instead of
    /// whole-second int, forwarded through <see cref="ReportPositionForItemCommand"/>.
    /// </summary>
    public async Task CurrentTimeForItemCommand(double? seconds, string? itemId)
    {
        if (seconds is null)
            return;
        await ReportPositionForItemCommand((long)Math.Round(seconds.Value * 1000), itemId);
    }

    /// <summary>
    /// Item-tagged twin of <see cref="ReportPositionCommand"/>: identical accept
    /// path (active-device + IgnoreCurrentTimeUntil gating), plus one extra guard —
    /// when <paramref name="itemId"/> is supplied and does not match
    /// <see cref="MusicPlayerState.CurrentItem"/>'s id, the report is dropped
    /// outright (no mutation, no broadcast). This closes the track-boundary race
    /// an untagged report cannot defend against: an in-flight position report for
    /// a track that has since changed (auto-advance, skip, crossfade) would
    /// otherwise silently overwrite the new track's fresh Time with a stale value —
    /// the "mirror shows the previous item's position" bug. A null itemId behaves
    /// exactly like <see cref="ReportPositionCommand"/> — untagged reports keep
    /// working unchanged; they simply forgo the stale-item check.
    /// </summary>
    public async Task ReportPositionForItemCommand(long? positionMs, string? itemId)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        if (positionMs is null)
            return;

        // See MusicPlaybackService.WithStateLockAsync's own doc: this is the
        // item-tagged twin of ReportPositionCoreAsync and shares the same fix —
        // the same per-user lock StartPlaybackTimer's own tick takes.
        await _musicPlaybackService.WithStateLockAsync(
            user.Id,
            async () =>
            {
                if (!_musicPlayerStateManager.TryGetValue(user.Id, out MusicPlayerState? playerState))
                {
                    await _musicPlaybackService.UpdatePlaybackState(user, playerState);
                    return;
                }

                ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? caller);

                // Liveness first, ahead of both drop gates below: a report from the ACTIVE
                // device that is about to be rejected as stale-item or as landing inside
                // the ignore window still proves the device is alive right now. Only the
                // device the server considers active may prove the session is genuinely
                // still playing somewhere or move the authoritative position — a
                // stray/passive report must never mask a truly-dead active device from
                // MusicPlaybackService's staleness sweep. A passive report is a complete
                // no-op for both liveness and position.
                if (!MusicPlaybackService.TryRefreshHeartbeat(playerState, caller?.DeviceId))
                    return;

                if (!MusicPlaybackService.IsReportForCurrentItem(playerState, itemId))
                    return;

                if (DateTime.UtcNow < playerState.IgnoreCurrentTimeUntil)
                    return;

                playerState.SetPosition((int)positionMs.Value);

                await _musicPlaybackService.UpdatePlaybackState(user, playerState);
            }
        );
    }

    public long GetServerTime()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Called by the active client when it begins its crossfade volume ramp (typically 3 s
    /// before the current track ends).  Suppresses the server's 100 ms auto-advance timer for
    /// this user so the server does not race the client and interrupt the fade.
    /// </summary>
    /// <param name="fadeDurationMs">
    /// How long the client's fade takes in milliseconds (e.g. 3000).  The server adds a 5 s
    /// safety margin on top; if <c>CrossfadeCompleteCommand</c> never arrives within that window
    /// the server force-advances anyway.
    /// </param>
    public Task CrossfadeStartCommand(int? fadeDurationMs)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return Task.CompletedTask;

        if (fadeDurationMs is null)
            return Task.CompletedTask;

        if (!ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? client))
            return Task.CompletedTask;

        _musicPlaybackService.StartCrossfade(user.Id, client.DeviceId, fadeDurationMs.Value);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called by the active client once its crossfade is complete and the new track is fully
    /// playing.  The server advances state to <paramref name="newTrackId"/>, resets progress to
    /// zero, and broadcasts the updated state to all connected clients.
    /// </summary>
    /// <param name="newTrackId">The <see cref="Guid"/> of the track that is now playing.</param>
    public async Task CrossfadeCompleteCommand(Guid? newTrackId)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        if (newTrackId is null)
        {
            _logger.LogWarning(
                "{Name}: [MusicHub.CrossfadeCompleteCommand] ignored — newTrackId was null",
                user.Name
            );
            return;
        }

        if (!ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? client))
            return;

        await _musicPlaybackService.CompleteCrossfade(user, client.DeviceId, newTrackId.Value);
    }
}
