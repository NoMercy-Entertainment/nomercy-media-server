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

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Music;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Playback;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Services.Music;

public class MusicPlaybackService
{
    private readonly MusicPlayerStateManager _stateManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly IClientMessenger _clientMessenger;
    private readonly IEventBus? _eventBus;
    private readonly MusicActiveDeviceRegistry _activeDeviceRegistry;
    private readonly ILogger<MusicPlaybackService>? _logger;
    private readonly string[] _repeatStates = ["off", "one", "all"];
    private static int PlayerStateEventId => Interlocked.Increment(ref field);

    public MusicPlaybackService(
        MusicPlayerStateManager stateManager,
        IServiceProvider serviceProvider,
        IClientMessenger clientMessenger,
        MusicActiveDeviceRegistry activeDeviceRegistry,
        IEventBus? eventBus = null,
        ILogger<MusicPlaybackService>? logger = null
    )
    {
        _stateManager = stateManager;
        _serviceProvider = serviceProvider;
        _clientMessenger = clientMessenger;
        _activeDeviceRegistry = activeDeviceRegistry;
        _eventBus = eventBus;
        _logger = logger;
    }

    private readonly ConcurrentDictionary<Guid, Timer> _timers = new();
    private readonly ConcurrentDictionary<Guid, Timer> _broadcastTimers = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _stateLocks = new();
    private readonly ConcurrentDictionary<Guid, byte> _playbackStartsInFlight = new();

    // Fingerprint of the last MusicPlayerState broadcast actually sent per user —
    // see ShouldCoalesceBroadcast, which drops a redundant repeat of it.
    private readonly ConcurrentDictionary<Guid, BroadcastFingerprint> _lastBroadcastFingerprints =
        new();
    private const int TimerInterval = 100;
    private const int CrossfadeLeewayMs = 10000; // Send PrepareCrossfade 10s before track end (gives time to buffer)
    private const int BroadcastDebounceMs = 150;
    private const int MinPlayTimeForRecordMs = 10_000;

    // Window within which two broadcasts for the same user, same track, same
    // play/pause state, and the same second-granularity position are treated
    // as the redundant echo of one real state change rather than two distinct
    // ones — collapsing the bursts the many ungated UpdatePlaybackState call
    // sites (StartPlayback, HandleTrackCompletion, ApplyItemLikeAsync, command
    // handlers, ...) can produce within milliseconds of each other. This is a
    // volume reduction only: out-of-order delivery is corrected by
    // MusicPlayerState.Seq, not by this window, so it stays short and
    // conservative — when unsure, UpdatePlaybackState emits.
    private const int BroadcastCoalesceWindowMs = 50;
    private const int BroadcastTimeBucketMs = 1000;

    // Both the Android and web clients throttle position reports to roughly once
    // every 5s while playing (MusicHubAdapter/MusicPlayerEventBridge on Android,
    // audioPlayer.ts on web all gate on ">5s since last send"). Three missed
    // reports tolerates normal network jitter or a dropped packet without a false
    // positive, while still recovering within one refresh cycle of a device that
    // actually went dark — a backgrounded/crashed player, or a Cast Receiver whose
    // WebSocket lingers after its cast session has effectively ended.
    internal const int ActiveDeviceStaleTimeoutMs = 15_000;

    // The window a device gets to prove life for the FIRST time after being handed
    // playback, before it has ever reported. A device that reported and went quiet
    // is dead and 15s finds it; a device that has never reported may simply not be
    // up yet. A TV woken from sleep by Cast has to boot, connect its hub, take
    // ownership and start its engine before a single position report exists —
    // measured on a Nokia Streaming Box, that was 18s from handoff to first sample,
    // so the short timeout ended the session three seconds before the music
    // reached the speakers and the whole cast read as a failure.
    internal const int ActiveDeviceFirstReportTimeoutMs = 45_000;

    // Safety margin added on top of the client-reported fadeDuration when computing
    // CrossfadeTimeout.  If CrossfadeComplete never arrives within that window the server
    // forces auto-advance anyway (handles client disconnect / network loss mid-crossfade).
    private const int CrossfadeSafetyMarginMs = 5000;

    private SemaphoreSlim GetStateLock(Guid userId)
    {
        return _stateLocks.GetOrAdd(userId, _ => new(1, 1));
    }

    /// <summary>
    /// Runs <paramref name="action"/> under the same per-user lock
    /// <see cref="StartPlaybackTimer"/>'s own 100ms tick already takes before it
    /// touches <see cref="MusicPlayerState"/>. Every hub method that mutates that
    /// state took SOME lock before this existed — <c>PlaybackCommand</c> and
    /// <c>StartPlaybackCommand</c> via <c>GetUserLock</c>, the tick via this one
    /// — except <c>ReportPositionCoreAsync</c>/<c>ReportPositionForItemCommand</c>,
    /// which took neither. A position report landing while the tick is mid
    /// <see cref="HandleTrackCompletion"/>/<see cref="UpdateStateBasedOnRepeatMode"/>
    /// (several sequential field writes — CurrentItem, Playlist, Backlog, Time,
    /// PlayState — none of them one atomic step together) raced
    /// <see cref="MusicPlayerState.CloneForBroadcast"/> against a half-written
    /// state and could broadcast a torn combination: a PlayState from before the
    /// tick's mutation next to a CurrentItem from after it, or vice versa. Both
    /// callers now route their read-mutate-broadcast through here.
    /// </summary>
    internal async Task WithStateLockAsync(Guid userId, Func<Task> action)
    {
        SemaphoreSlim stateLock = GetStateLock(userId);
        await stateLock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            stateLock.Release();
        }
    }

    internal void StartPlaybackTimer(User user)
    {
        if (_timers.TryGetValue(user.Id, out Timer? existingTimer))
            existingTimer.Dispose();

        if (!_stateManager.TryGetValue(user.Id, out MusicPlayerState? playerState))
            return;

        // Any (re)start of the ticking loop counts as proof of life, regardless of
        // who asked for it. Playback can be legitimately resumed remotely from any
        // connected device (not just the active one), so gating this on caller
        // identity would either reject a valid remote resume or reintroduce the
        // exact bug this exists to prevent: resuming after a long pause must not
        // make the active device look instantly stale to the check below.
        playerState.LastActiveHeartbeatUtc = DateTime.UtcNow;

        SemaphoreSlim stateLock = GetStateLock(user.Id);

        Timer timer = new(
            async _ =>
            {
                if (!await stateLock.WaitAsync(0))
                    return; // Skip tick if previous tick is still running
                try
                {
                    if (!_stateManager.TryGetValue(user.Id, out MusicPlayerState? playerState))
                        return;
                    if (!playerState.PlayState || playerState.CurrentItem is null)
                        return;

                    if (
                        IsActiveDeviceStale(playerState, DateTime.UtcNow)
                        && !IsPlaybackStartInFlight(user.Id)
                    )
                    {
                        await EndStaleActiveSessionAsync(user, playerState);
                        return;
                    }

                    playerState.Time += TimerInterval;

                    int duration = playerState.CurrentItem.Duration.ToMilliSeconds();

                    if (
                        playerState.Time >= (duration / 2)
                        && playerState.Time < (duration / 2) + TimerInterval
                        && playerState.Time >= MinPlayTimeForRecordMs
                    )
                    {
                        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
                        IDbContextFactory<MediaContext> factory =
                            scope.ServiceProvider.GetRequiredService<
                                IDbContextFactory<MediaContext>
                            >();
                        await using MediaContext ctx = await factory.CreateDbContextAsync();
                        await ctx.MusicPlays.AddAsync(new(user.Id, playerState.CurrentItem.Id));
                        await ctx.SaveChangesAsync();
                        await PublishProgressEventAsync(user.Id, playerState);
                    }

                    // Send crossfade signal to clients ~10s before track ends
                    if (
                        playerState.Time >= duration - CrossfadeLeewayMs
                        && !playerState.CrossfadeSignalSent
                    )
                    {
                        playerState.CrossfadeSignalSent = true;
                        PlaylistTrackDto? nextItem = PeekNextTrack(playerState);
                        if (nextItem != null)
                            await SendPrepareCrossfadeSignal(user, nextItem);
                    }

                    if (playerState.Time >= duration)
                    {
                        // If a client has taken ownership of the crossfade, suppress server
                        // auto-advance and wait for CrossfadeComplete (or the safety timeout).
                        if (playerState.IsCrossfading)
                        {
                            if (DateTime.UtcNow < playerState.CrossfadeTimeout)
                                return; // Still within the allowed crossfade window — do nothing
                            // Safety timeout expired: client never sent CrossfadeComplete.
                            // Clear the crossfade flag and fall through to normal auto-advance.
                            playerState.IsCrossfading = false;
                            playerState.CrossfadeDeviceId = null;
                        }

                        await HandleTrackCompletion(user, playerState);
                    }
                }
                finally
                {
                    stateLock.Release();
                }
            },
            null,
            100,
            TimerInterval
        );

        _timers[user.Id] = timer;
    }

    public void RemoveTimer(Guid userId)
    {
        if (_timers.TryRemove(userId, out Timer? timer))
            timer.Dispose();
    }

    /// <summary>
    /// Marks a <see cref="NoMercy.Api.Hubs.MusicHub.StartPlaybackCommand"/> as in flight for
    /// <paramref name="userId"/> — see <see cref="IsPlaybackStartInFlight"/> for why the
    /// staleness watchdog needs to know this. Also stamps a fresh heartbeat immediately if a
    /// session already exists, covering a slow playlist fetch on an EXISTING session: the old
    /// timer from before this call keeps ticking through the whole fetch, and without this
    /// stamp it would only see the stale pre-fetch heartbeat until the fetch finishes.
    /// Call from a try/finally around the whole command body, paired with
    /// <see cref="EndPlaybackStart"/>.
    /// </summary>
    internal void BeginPlaybackStart(Guid userId)
    {
        _playbackStartsInFlight[userId] = 0;

        if (_stateManager.TryGetValue(userId, out MusicPlayerState? state))
            state.LastActiveHeartbeatUtc = DateTime.UtcNow;
    }

    internal void EndPlaybackStart(Guid userId)
    {
        _playbackStartsInFlight.TryRemove(userId, out _);
    }

    /// <summary>
    /// True while a <see cref="NoMercy.Api.Hubs.MusicHub.StartPlaybackCommand"/> is in flight for
    /// <paramref name="userId"/>. SignalR dispatches invocations on one connection serially by
    /// default, so a slow playlist fetch (contended DB, cold cache, a large artist) starves that
    /// same connection's queued position reports for its entire duration — the active device is
    /// very much alive, just queued behind its own start command. The 100ms watchdog tick
    /// consults this before killing a session on staleness so it can't race a legitimate,
    /// still-in-progress start; once the flag clears, a genuinely dead device is still caught on
    /// the very next tick against whatever heartbeat <see cref="BeginPlaybackStart"/> or a real
    /// report last stamped.
    /// </summary>
    internal bool IsPlaybackStartInFlight(Guid userId)
    {
        return _playbackStartsInFlight.ContainsKey(userId);
    }

    /// <summary>
    /// True once the session says it's playing but the active device hasn't proven
    /// life (see <see cref="StartPlaybackTimer"/> and
    /// <see cref="NoMercy.Api.Hubs.MusicHub.ReportPositionCommand"/>) for
    /// <see cref="ActiveDeviceStaleTimeoutMs"/>. Deliberately false while paused —
    /// a paused/stopped session has no reporting cadence to fall behind on, and
    /// <see cref="MusicPlaybackCommandHandler.HandleStop"/> already relies on the
    /// active device staying sticky through a deliberate stop to block hijack;
    /// treating "no report since I stopped" as staleness would undo that on a
    /// timer.
    /// </summary>
    internal static bool IsActiveDeviceStale(MusicPlayerState state, DateTime nowUtc)
    {
        if (!state.PlayState || string.IsNullOrEmpty(state.DeviceId))
            return false;

        int timeoutMs = state.ActiveDeviceProvenAlive
            ? ActiveDeviceStaleTimeoutMs
            : ActiveDeviceFirstReportTimeoutMs;

        return nowUtc - state.LastActiveHeartbeatUtc > TimeSpan.FromMilliseconds(timeoutMs);
    }

    /// <summary>
    /// True when callerDeviceId is the device MusicPlayerState.DeviceId currently
    /// names as active. Used to make sure only the active device's own position
    /// reports can refresh <see cref="MusicPlayerState.LastActiveHeartbeatUtc"/> —
    /// a stray report from a passive client must never mask a truly-dead active
    /// device from <see cref="IsActiveDeviceStale"/>.
    /// </summary>
    internal static bool IsCallerTheActiveDevice(MusicPlayerState state, string? callerDeviceId)
    {
        return !string.IsNullOrEmpty(state.DeviceId)
            && !string.IsNullOrEmpty(callerDeviceId)
            && state.DeviceId.Equals(callerDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Single entry point every inbound hub call that can prove the active device is
    /// alive should route through — a position report, a playback command, or a cheap
    /// state read. Refreshes <see cref="MusicPlayerState.LastActiveHeartbeatUtc"/> and
    /// returns true only when <paramref name="callerDeviceId"/> is
    /// <see cref="IsCallerTheActiveDevice"/>; a passive caller is a complete no-op so it
    /// can never mask a truly-dead active device from <see cref="IsActiveDeviceStale"/>
    /// nor (via the caller's own use of the return value) overwrite the active device's
    /// authoritative position with its own.
    /// </summary>
    internal static bool TryRefreshHeartbeat(MusicPlayerState state, string? callerDeviceId)
    {
        if (!IsCallerTheActiveDevice(state, callerDeviceId))
            return false;

        state.LastActiveHeartbeatUtc = DateTime.UtcNow;
        state.ActiveDeviceProvenAlive = true;
        return true;
    }

    /// <summary>
    /// Gate for <see cref="NoMercy.Api.Hubs.MusicHub.ReportPositionForItemCommand"/>:
    /// a null <paramref name="itemId"/> is untagged and always passes (no stale
    /// check possible, matches the untagged commands' existing behavior); a
    /// non-null itemId must parse as a Guid and match
    /// <see cref="MusicPlayerState.CurrentItem"/>'s id (an unparsable id or no
    /// current item both count as a mismatch). Case-insensitive by virtue of
    /// comparing parsed Guids rather than raw strings.
    /// </summary>
    internal static bool IsReportForCurrentItem(MusicPlayerState state, string? itemId)
    {
        if (itemId is null)
            return true;

        return state.CurrentItem is not null
            && Guid.TryParse(itemId, out Guid parsed)
            && parsed == state.CurrentItem.Id;
    }

    /// <summary>
    /// The active device stopped proving life while the session was still marked
    /// playing — a backgrounded/crashed player or an anonymous Cast Receiver that
    /// never cleanly disconnects. Ends the session outright (mirrors
    /// <see cref="MusicPlaybackCommandHandler.HandleStop"/>'s shape) rather than
    /// auto-promoting another connected-but-passive device: silently starting
    /// audio somewhere the user never asked for it would be a worse surprise than
    /// the bug this fixes. Clearing DeviceId — unlike a deliberate stop, which
    /// keeps it to block hijack — is the deliberate difference: the device itself
    /// is presumed gone, so the very next StartPlaybackCommand from any connected
    /// device must be free to become active immediately.
    /// </summary>
    internal async Task EndStaleActiveSessionAsync(User user, MusicPlayerState state)
    {
        string? staleDeviceId = state.DeviceId;

        _logger?.LogWarning(
            "Music session for user {UserId}: active device {DeviceId} stopped reporting position for over {TimeoutMs}ms — ending the session",
            [user.Id, staleDeviceId, ActiveDeviceStaleTimeoutMs]
        );

        RemoveTimer(user.Id);

        if (!string.IsNullOrEmpty(staleDeviceId))
            _activeDeviceRegistry.RemoveIfMatches(user.Id, staleDeviceId);

        state.CurrentItem = null;
        state.PlayState = false;
        state.SetPosition(0);
        state.Backlog = [];
        state.Playlist = [];
        state.CurrentList = new("", UriKind.Relative);
        state.DeviceId = null;
        state.Actions = new()
        {
            Disallows = new()
            {
                Previous = true,
                Next = true,
                Resuming = true,
                Pausing = true,
                Seeking = true,
                Stopping = true,
                Muting = true,
                TogglingShuffle = true,
                TogglingRepeatContext = true,
                TogglingRepeatTrack = true,
            },
        };

        await UpdatePlaybackState(user, state);
    }

    /// <summary>
    /// Called by the active client (via <see cref="NoMercy.Api.Hubs.MusicHub.CrossfadeStartCommand"/>)
    /// when it begins the crossfade volume ramp.  Suppresses server auto-advance for this user
    /// for <paramref name="fadeDurationMs"/> + <see cref="CrossfadeSafetyMarginMs"/> milliseconds.
    /// Only the device matching <paramref name="deviceId"/> may later complete or override the
    /// suppression; competing commands from other devices are ignored.
    /// </summary>
    public void StartCrossfade(Guid userId, string deviceId, int fadeDurationMs)
    {
        if (!_stateManager.TryGetValue(userId, out MusicPlayerState? state))
            return;

        state.IsCrossfading = true;
        state.CrossfadeDeviceId = deviceId;
        state.CrossfadeTimeout = DateTime.UtcNow.AddMilliseconds(
            fadeDurationMs + CrossfadeSafetyMarginMs
        );
    }

    /// <summary>
    /// Called by the active client (via <see cref="NoMercy.Api.Hubs.MusicHub.CrossfadeCompleteCommand"/>)
    /// once the crossfade has finished and the new track is fully playing.  Sets the server state
    /// to the new track, resets time to zero, clears the crossfade suppression, and broadcasts
    /// the updated state to all connected clients.
    /// </summary>
    public async Task CompleteCrossfade(User user, string deviceId, Guid newTrackId)
    {
        if (!_stateManager.TryGetValue(user.Id, out MusicPlayerState? state))
            return;

        // Only accept from the device that started the crossfade.
        if (state.CrossfadeDeviceId != deviceId)
            return;

        // Locate the new track.  It must be at the head of the upcoming playlist.
        PlaylistTrackDto? newTrack = state.Playlist.FirstOrDefault(t => t.Id == newTrackId);
        if (newTrack is null)
        {
            // Track not found — clear the flag and let the server do its own advance on the
            // next timer tick (Time is already >= Duration at this point).
            state.IsCrossfading = false;
            state.CrossfadeDeviceId = null;
            return;
        }

        RemoveTimer(user.Id);

        int currentIndex = state.Playlist.IndexOf(newTrack);

        // Move the current item to the backlog.
        if (state.CurrentItem != null)
            state.Backlog.Add(state.CurrentItem);

        // Move all tracks before newTrack to the backlog (they were skipped).
        for (int i = 0; i < currentIndex; i++)
            state.Backlog.Add(state.Playlist[i]);

        state.Playlist.RemoveRange(0, currentIndex + 1);
        state.CurrentItem = newTrack;
        state.SetPosition(0);
        state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
        state.CrossfadeSignalSent = false;
        state.IsCrossfading = false;
        state.CrossfadeDeviceId = null;
        state.PlayState = true;

        await UpdatePlaybackState(user, state);
        StartPlaybackTimer(user);
    }

    public void DebouncedUpdatePlaybackState(User user, MusicPlayerState state)
    {
        if (_broadcastTimers.TryRemove(user.Id, out Timer? existing))
            existing.Dispose();

        // Epoch-ms so this line correlates directly against the scheduling call's
        // own "broadcastMs" mark in MusicHub.PlaybackCommand — that mark is only
        // when the Timer was ARMED; this is when the relay actually fired and sent.
        long scheduledMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Timer timer = new(
            async _ =>
            {
                _broadcastTimers.TryRemove(user.Id, out Timer? _);
                long firedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await UpdatePlaybackState(user, state);
                long sentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                _logger?.LogInformation(
                    "{UserId}: [MusicPlaybackService.DebouncedUpdatePlaybackState] scheduledMs={ScheduledMs} firedMs={FiredMs} (+{FireDelayMs}ms) sentMs={SentMs} (+{SendMs}ms) totalMs={TotalMs}ms playlist={PlaylistCount} backlog={BacklogCount}",
                    [
                        user.Id,
                        scheduledMs,
                        firedMs,
                        firedMs - scheduledMs,
                        sentMs,
                        sentMs - firedMs,
                        sentMs - scheduledMs,
                        state.Playlist.Count,
                        state.Backlog.Count,
                    ]
                );
            },
            null,
            BroadcastDebounceMs,
            Timeout.Infinite
        );

        _broadcastTimers[user.Id] = timer;
    }

    private async Task HandleTrackCompletion(User user, MusicPlayerState state)
    {
        if (state.CurrentItem == null)
            return;
        RemoveTimer(user.Id);

        int currentIndex = state.Playlist.IndexOf(state.CurrentItem);

        bool wasLastTrack = state.Repeat == "off" && currentIndex + 1 >= state.Playlist.Count;
        if (wasLastTrack)
        {
            await PublishCompletedEventAsync(user.Id, state);
        }

        UpdateStateBasedOnRepeatMode(state, currentIndex);
        state.CrossfadeSignalSent = false; // Reset for next track

        await UpdatePlaybackState(user, state);
        StartPlaybackTimer(user);
    }

    public async Task ApplyItemLikeAsync(
        Guid userId,
        Guid itemId,
        bool liked,
        CancellationToken cancellationToken = default
    )
    {
        if (!_stateManager.TryGetValue(userId, out MusicPlayerState? playerState))
            return;

        if (playerState.CurrentItem is not null && playerState.CurrentItem.Id == itemId)
            playerState.CurrentItem.Favorite = liked;

        foreach (PlaylistTrackDto track in playerState.Playlist)
            if (track.Id == itemId)
                track.Favorite = liked;

        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        IUserRepository userRepository =
            scope.ServiceProvider.GetRequiredService<IUserRepository>();
        User? user = await userRepository.GetByIdAsync(userId);
        if (user is null)
            return;

        await UpdatePlaybackState(user, playerState);
    }

    public async Task UpdatePlaybackState(User user, MusicPlayerState? state)
    {
        MusicPlayerState? broadcastState = null;
        if (state is not null)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (ShouldCoalesceBroadcast(user.Id, state, nowMs))
                return;

            // Same instant for both: Timestamp predates ServerTimeMs and some
            // clients may already key off it, so it stays; ServerTimeMs is the
            // new clock-sync reference every broadcast emit carries.
            state.Timestamp = nowMs;
            state.ServerTimeMs = nowMs;

            // Per-user monotonic, clock-anchored sequence stamped fresh on
            // every emit that reaches here — see MusicPlayerStateManager.NextSeq
            // and MusicPlayerState.Seq. Lets a client drop a broadcast that
            // arrives out of order instead of racing it against one it already
            // applied.
            state.Seq = _stateManager.NextSeq(user.Id);

            // Broadcast a lyric-stripped queue projection, never the stored
            // state — see MusicPlayerState.CloneForBroadcast for why queue-item
            // lyrics are dead weight on this ~per-5s broadcast.
            broadcastState = state.CloneForBroadcast();
        }

        EventPayload<PlayerStateEventElement> payload = new()
        {
            Events =
            [
                new()
                {
                    Event = new() { EventId = PlayerStateEventId, State = broadcastState },
                    Source = "musicHub",
                    Type = MusicEventType.PlayerStateChanged,
                    User = user,
                },
            ],
        };

        await _clientMessenger.SendTo("MusicPlayerState", "musicHub", user.Id, payload);
    }

    /// <summary>
    /// Drops a MusicPlayerState broadcast that is the redundant echo of the one
    /// immediately before it for this user: same track, same play/pause state,
    /// same active device, same second-granularity position, within
    /// <see cref="BroadcastCoalesceWindowMs"/> of the previous emit. Only
    /// volume is reduced here — out-of-order correctness is carried entirely by
    /// <see cref="MusicPlayerState.Seq"/>, so this stays conservative and never
    /// coalesces across a real change or a wider time gap.
    /// </summary>
    private bool ShouldCoalesceBroadcast(Guid userId, MusicPlayerState state, long nowMs)
    {
        BroadcastFingerprint fingerprint = new(
            state.CurrentItem?.Id,
            state.PlayState,
            state.DeviceId,
            state.Time / BroadcastTimeBucketMs,
            nowMs
        );

        if (
            _lastBroadcastFingerprints.TryGetValue(
                userId,
                out BroadcastFingerprint previousFingerprint
            )
            && previousFingerprint.ItemId == fingerprint.ItemId
            && previousFingerprint.PlayState == fingerprint.PlayState
            && previousFingerprint.DeviceId == fingerprint.DeviceId
            && previousFingerprint.TimeBucket == fingerprint.TimeBucket
            && nowMs - previousFingerprint.AtMs < BroadcastCoalesceWindowMs
        )
            return true;

        _lastBroadcastFingerprints[userId] = fingerprint;
        return false;
    }

    // DeviceId is part of the fingerprint precisely so a ChangeDeviceCommand
    // is never mistaken for the echo it otherwise looks identical to: the
    // track and play state usually don't change on a handoff, only which
    // device is active does — and that's the one thing a demoted device
    // needs this broadcast for. Without it, a device switch landing within
    // BroadcastCoalesceWindowMs of the prior broadcast (a stray position
    // report, another command) was silently dropped whole, so the device
    // being demoted never heard it and kept playing indefinitely alongside
    // the new active one. Stoney, 2026-09-07, reproduced live by picking a
    // second TV from the device picker: "Only one TV should be playing."
    private readonly record struct BroadcastFingerprint(
        Guid? ItemId,
        bool PlayState,
        string? DeviceId,
        int TimeBucket,
        long AtMs
    );

    internal async Task PublishStartedEventAsync(Guid userId, MusicPlayerState state)
    {
        IEventBus? bus =
            _eventBus ?? (EventBusProvider.IsConfigured ? EventBusProvider.Current : null);
        if (bus is null || state.CurrentItem is null)
            return;

        await bus.PublishAsync(
            new PlaybackStartedEvent
            {
                UserId = userId,
                MediaId = 0,
                MediaIdentifier = state.CurrentItem.Id.ToString(),
                MediaType = "music",
                DeviceId = state.DeviceId,
            }
        );
    }

    private async Task PublishProgressEventAsync(Guid userId, MusicPlayerState state)
    {
        IEventBus? bus =
            _eventBus ?? (EventBusProvider.IsConfigured ? EventBusProvider.Current : null);
        if (bus is null || state.CurrentItem is null)
            return;

        int duration = state.CurrentItem.Duration.ToMilliSeconds();

        await bus.PublishAsync(
            new PlaybackProgressUpdatedEvent
            {
                UserId = userId,
                MediaId = 0,
                MediaIdentifier = state.CurrentItem.Id.ToString(),
                Position = TimeSpan.FromMilliseconds(state.Time),
                Duration = TimeSpan.FromMilliseconds(duration),
            }
        );
    }

    private async Task PublishCompletedEventAsync(Guid userId, MusicPlayerState state)
    {
        IEventBus? bus =
            _eventBus ?? (EventBusProvider.IsConfigured ? EventBusProvider.Current : null);
        if (bus is null || state.CurrentItem is null)
            return;

        await bus.PublishAsync(
            new PlaybackCompletedEvent
            {
                UserId = userId,
                MediaId = 0,
                MediaIdentifier = state.CurrentItem.Id.ToString(),
                MediaType = "music",
            }
        );
    }

    private static PlaylistTrackDto? PeekNextTrack(MusicPlayerState state)
    {
        if (state.Repeat == "one")
            return null; // Same track restarts, no crossfade

        if (state.Playlist.Count > 0)
            return state.Playlist.First();

        if (state is { Repeat: "all", Backlog.Count: > 0 })
            return state.Backlog.First(); // Will loop around

        return null; // No next track (playback will stop)
    }

    private async Task SendPrepareCrossfadeSignal(User user, PlaylistTrackDto nextItem)
    {
        var payload = new
        {
            next_item = nextItem,
            crossfade_duration_ms = 3000,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await _clientMessenger.SendTo("PrepareCrossfade", "musicHub", user.Id, payload);
    }

    private void UpdateStateBasedOnRepeatMode(MusicPlayerState state, int currentIndex)
    {
        switch (state.Repeat)
        {
            case "one":
                state.SetPosition(0);
                break;
            case "all":
                if (currentIndex == state.Playlist.Count - 1)
                {
                    // Move the current item to the backlog
                    if (state.CurrentItem != null)
                        state.Backlog.Add(state.CurrentItem);

                    // Move the backlog to the playlist and start from the beginning
                    state.Playlist = [.. state.Backlog];
                    state.Backlog.Clear();

                    if (state.Playlist.Count > 0)
                    {
                        state.CurrentItem = state.Playlist.First();
                        state.Playlist.RemoveAt(0);
                        state.SetPosition(0);
                        state.PlayState = true;
                    }
                    else
                    {
                        // If the playlist is empty, stop playback
                        state.PlayState = false;
                        state.SetPosition(0);
                        state.CurrentItem = null;
                    }
                }
                else
                {
                    if (state.CurrentItem != null)
                        state.Backlog.Add(state.CurrentItem);
                    state.CurrentItem = state.Playlist[currentIndex + 1];
                    state.Playlist.RemoveAt(currentIndex + 1);
                    state.SetPosition(0);
                }

                break;
            default:
                if (state.CurrentItem != null)
                    state.Backlog.Add(state.CurrentItem);
                if (currentIndex + 1 < state.Playlist.Count)
                {
                    state.PlayState = true;
                    state.SetPosition(0);
                    state.CurrentItem = state.Playlist[currentIndex + 1];
                    state.Playlist.RemoveAt(currentIndex + 1);
                }
                else
                {
                    state.PlayState = false;
                    state.SetPosition(0);
                    state.CurrentItem = null;
                }

                break;
        }
    }
}
