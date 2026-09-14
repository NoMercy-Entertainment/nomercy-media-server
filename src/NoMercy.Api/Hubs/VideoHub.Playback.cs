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

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Services.Video;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Http;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.Setup.Cast;

namespace NoMercy.Api.Hubs;

public partial class VideoHub
{
    public async Task SetTime(VideoProgressRequest request)
    {
        Guid userId = Context.User.UserId();

        User? user = UserCacheService.Users.FirstOrDefault(x => x.Id.Equals(userId));

        if (user is null)
            return;

        // The player that is rendering the video owns the clock; this report is the
        // only thing that moves the session forward.
        if (_videoPlayerStateManager.TryGetValue(user.Id, out VideoPlayerState? playerState))
            await _videoPlaybackService.ApplyClientProgress(user, playerState, request.Time * 1000);

        await _userDataRepository.UpsertWatchProgressAsync(
            new(
                user.Id,
                request.PlaylistType,
                Convert.ToString((object?)request.PlaylistId) ?? string.Empty,
                request.TmdbId,
                request.VideoId,
                request.Time,
                request.Audio,
                request.Subtitle,
                request.SubtitleType
            )
        );
    }

    public async Task RemoveWatched(VideoProgressRequest request)
    {
        string? guid = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(guid, out Guid userId))
            return;

        User? user = UserCacheService.Users.FirstOrDefault(x => x.Id.Equals(userId));

        if (user is null)
            return;

        // Scope the delete to the single requested item by its typed id. The old
        // predicate OR-ed MovieId/TvId/SpecialId/CollectionId == the request ids;
        // because a movie row has null Tv/Special/Collection ids (and vice versa),
        // a null request id matched EVERY row of that type — so finishing/removing
        // one item wiped the user's whole continue-watching list.
        int? intId = request.PlaylistType switch
        {
            MediaTypes.MovieMediaType or MediaTypes.TvMediaType or MediaTypes.CollectionMediaType =>
                request.TmdbId,
            _ => null,
        };
        Ulid? ulidId =
            request.PlaylistType == MediaTypes.SpecialMediaType ? request.SpecialId : null;

        await _userDataRepository.RemoveForItemAsync(user.Id, request.PlaylistType, intId, ulidId);
    }

    public async Task StartPlaybackCommand(string? type, dynamic? listId, int? itemId)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        if (string.IsNullOrEmpty(type) || listId is null)
        {
            _logger.LogWarning(
                "{Name}: [VideoHub.StartPlaybackCommand] ignored — null arg (type='{Null}', listId={Set})",
                user.Name,
                type ?? "<null>",
                (listId is null ? "<null>" : "set")
            );
            return;
        }

        string language = GetLanguageFromContext();
        string country = GetCountryFromContext();

        try
        {
            dynamic? playlistResult = await _videoPlaylistManager.GetPlaylist(
                user.Id,
                type,
                listId,
                itemId,
                language,
                country
            );

            await HandlePlaybackState(
                user,
                type,
                listId,
                playlistResult.Item1,
                playlistResult.Item2
            );
        }
        catch (ArgumentException ex)
        {
            _logger.LogInformation("Invalid playlist type: {Message}", ex.Message);

            User? user2 = UserCacheService.GetUser(Context.User.UserId());
            if (user2 is not null)
            {
                ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? client2);
                Ulid deviceId2 = client2?.Id ?? Ulid.Empty;
                try
                {
                    await ActivityLogger.LogFailureAsync(
                        "failure.playback_start",
                        user2.Id,
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
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Error in StartPlaybackCommand");
            _logger.LogError(ex, ex.Message);

            User? user2 = UserCacheService.GetUser(Context.User.UserId());
            if (user2 is not null)
            {
                ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? client2);
                Ulid deviceId2 = client2?.Id ?? Ulid.Empty;
                try
                {
                    await ActivityLogger.LogFailureAsync(
                        "failure.playback_start",
                        user2.Id,
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
        }
    }

    private async Task HandlePlaybackState(
        User user,
        string type,
        dynamic listId,
        VideoPlaylistResponseDto item,
        List<VideoPlaylistResponseDto> playlist
    )
    {
        VideoPlayerState? playerState = _videoPlayerStateManager.GetState(user.Id);

        if (
            playerState is null
            || playerState.CurrentItem is null
            || playerState.Playlist.Count == 0
        )
            await HandleNewPlayerState(user, type, listId, item, playlist);
        else if (IsCurrentPlaylist(playerState, type, listId, item.Id))
            await HandleExistingPlaylistState(user, playerState);
        else
            await HandlePlaylistChange(user, playerState, type, listId, item, playlist);
    }

    private async Task HandleNewPlayerState(
        User user,
        string type,
        dynamic listId,
        VideoPlaylistResponseDto item,
        List<VideoPlaylistResponseDto> playlist
    )
    {
        Device device = GetCallingDevice();
        VideoPlayerState videoPlayerState = await VideoPlayerStateFactory.Create(
            _contextFactory,
            user,
            device,
            item,
            playlist,
            type,
            listId
        );

        _videoPlayerStateManager.UpdateState(user.Id, videoPlayerState);
        await _videoPlaybackService.UpdatePlaybackState(user, videoPlayerState);
        await _videoPlaybackService.PublishStartedEventAsync(user.Id, videoPlayerState);

        try
        {
            await ActivityLogger.LogPlaybackAsync(
                "playback.started",
                user.Id,
                device.Id,
                item.VideoId,
                new { media_type = "video", title = item.Title }
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to log playback.started: {Message}", ex.Message);
        }
    }

    private Device GetCallingDevice() => ConnectedClients.Clients[Context.ConnectionId];

    private static bool IsCurrentPlaylist(
        VideoPlayerState state,
        string type,
        dynamic listId,
        int itemId
    )
    {
        return state.CurrentItem is not null
            && state.CurrentList.ToString().Contains($"{type}/{listId}")
            && state.CurrentItem?.Id == itemId;
    }

    private async Task HandleExistingPlaylistState(User user, VideoPlayerState state)
    {
        state.PlayState = true;

        state.Time = state.CurrentItem?.Progress?.Time * 1000 ?? 0;

        state.Actions.Disallows.Resuming = state.PlayState;
        state.Actions.Disallows.Pausing = !state.PlayState;
        state.Actions.Disallows.Stopping = false;
        state.Actions.Disallows.Seeking = false;
        state.Actions.Disallows.Muting = false;
        state.Actions.Disallows.Previous =
            state.CurrentItem is null || state.Playlist.IndexOf(state.CurrentItem) == 0;
        state.Actions.Disallows.Next =
            state.CurrentItem is null
            || state.Playlist.IndexOf(state.CurrentItem) == state.Playlist.Count - 1;
        UpdateDeviceInfo(state);
        await _videoPlaybackService.UpdatePlaybackState(user, state);
        await _videoPlaybackService.PublishStartedEventAsync(user.Id, state);
    }

    private async Task HandlePlaylistChange(
        User user,
        VideoPlayerState state,
        string type,
        dynamic listId,
        VideoPlaylistResponseDto item,
        List<VideoPlaylistResponseDto> playlist
    )
    {
        UpdateDeviceInfo(state);
        UpdatePlaylistInfo(state, type, listId, item, playlist);
        await _videoPlaybackService.UpdatePlaybackState(user, state);
        await _videoPlaybackService.PublishStartedEventAsync(user.Id, state);

        Device device = GetCallingDevice();
        try
        {
            await ActivityLogger.LogPlaybackAsync(
                "playback.started",
                user.Id,
                device.Id,
                item.VideoId,
                new { media_type = "video", title = item.Title }
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to log playback.started: {Message}", ex.Message);
        }
    }

    private void UpdateDeviceInfo(VideoPlayerState state)
    {
        if (!ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? device))
            return;
        state.DeviceId = device.DeviceId;
        state.VolumePercentage = device.VolumePercent ?? Device.DefaultVolumePercent;
    }

    private void UpdatePlaylistInfo(
        VideoPlayerState state,
        string type,
        dynamic listId,
        VideoPlaylistResponseDto item,
        List<VideoPlaylistResponseDto> playlist
    )
    {
        state.CurrentItem = item;
        state.PlayState = true;
        state.Playlist = playlist;
        state.CurrentList = new($"/{type}/{listId}/watch", UriKind.Relative);
        state.Time = item.Progress?.Time * 1000 ?? 0;
        state.Duration = item.Duration.ToMilliSeconds();
        state.Actions = new()
        {
            Disallows = new()
            {
                Stopping = false,
                Seeking = false,
                Muting = false,
                Pausing = !state.PlayState,
                Resuming = state.PlayState,
                Previous = playlist.IndexOf(item) == 0,
                Next = playlist.IndexOf(item) == playlist.Count - 1,
            },
        };
    }

    public VideoPlayerState? GetStateCommand()
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return null;

        _videoPlayerStateManager.TryGetValue(user.Id, out VideoPlayerState? playerState);
        if (playerState is null)
            return null;

        return playerState;
    }

    public async Task PlaybackCommand(string? command, object? data = null)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        if (string.IsNullOrEmpty(command))
        {
            _logger.LogWarning(
                "{Name}: [VideoHub.PlaybackCommand] ignored — command was null/empty",
                user.Name
            );
            return;
        }

        if (!_videoPlayerStateManager.TryGetValue(user.Id, out VideoPlayerState? state))
        {
            await _videoPlaybackService.UpdatePlaybackState(user, null);
            return;
        }

        ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? device);

        await _commandHandler.HandleCommand(user, command, data, state, device);

        if (state.DeviceId == null)
            if (device is not null)
            {
                state.DeviceId = device.DeviceId;
                state.VolumePercentage = device.VolumePercent ?? Device.DefaultVolumePercent;
            }

        await _videoPlaybackService.UpdatePlaybackState(user, state);
    }

    public async Task ChangeDeviceCommand(string? deviceId)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;

        if (string.IsNullOrEmpty(deviceId))
        {
            _logger.LogWarning(
                "{Name}: [VideoHub.ChangeDeviceCommand] ignored — deviceId was null/empty",
                user.Name
            );
            return;
        }

        // Extend connected-device list with owned TVs from the Devices table —
        // mirrors MusicHub.MusicDevicesAsync. Without this, the picker can't
        // hand video off to a sleeping TV. Live MusicHub clients are merged
        // with registered TV devices (online or not).
        List<Device> connectedDevices = Devices();
        await using (MediaContext ctx = await _contextFactory.CreateDbContextAsync())
        {
            List<Device> registeredTvs = await ctx
                .Devices.Where(d => d.OwnerUserId == user.Id && d.Type == "tv")
                .ToListAsync();

            HashSet<string> seenDeviceIds = new(
                connectedDevices.Select(d => d.DeviceId),
                StringComparer.OrdinalIgnoreCase
            );

            foreach (Device tv in registeredTvs)
                if (seenDeviceIds.Add(tv.DeviceId))
                    connectedDevices.Add(tv);
        }

        await _clientMessenger.SendTo(
            "ConnectedDevicesState",
            "videoHub",
            user.Id,
            connectedDevices
        );

        // TV-target branch: when handing off video to a TV, mint a cast session
        // bundle and LAUNCH the receiver. Mirrors MusicHub.ChangeDeviceCommand.
        // Cast Connect routes APK-installed TVs to the native APK and Web-only
        // TVs to cast.nomercy.tv — both consume customData.
        Device? targetTv = connectedDevices.FirstOrDefault(d =>
            d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase) && d.Type == "tv"
        );

        // A TV only ever seen from outside this network has no address a Cast LAUNCH could
        // reach, so the panel wake is skipped — never the handoff itself, which is what the
        // rest of this method performs.
        string? targetIp = targetTv is null
            ? null
            : CastAddress.Resolve(targetTv.LanIp, targetTv.Ip);

        if (targetTv is not null && targetIp is null)
            _logger.LogDebug(
                "No LAN address recorded for TV {DeviceId} — skipping panel wake",
                deviceId
            );

        if (targetTv is not null && targetIp is not null)
        {
            Ulid targetUlid = targetTv.Id;
            string serverIdString = Info.DeviceId.ToString();
            string serverUrl = CastLaunchOrigin.ServerUrl(_networkDiscovery);
            string locale = CastLaunchOrigin.SenderLocale(
                _httpContextAccessor.HttpContext?.Request.Headers.AcceptLanguage.ToString()
            );
            CastIntent intent = ResolveVideoIntent(user.Id);

            // A handoff always wakes the panel, so the target is treated as cold.
            _ = _castPanelWakeLauncher.LaunchIfColdAsync(
                targetIsLive: false,
                targetIp,
                useAndroidReceiver: _busRegistry.IsOnline(targetUlid),
                () =>
                    _castTokenService.MintAsync(
                        userId: user.Id,
                        serverId: serverIdString,
                        serverUrl: serverUrl,
                        deviceId: targetUlid,
                        intent: intent,
                        clientLocale: locale
                    )
            );
        }

        if (_videoPlayerStateManager.TryGetValue(user.Id, out VideoPlayerState? playerState))
        {
            playerState.DeviceId = deviceId;
        }
        else
        {
            await _videoPlaybackService.UpdatePlaybackState(user, playerState);
            return;
        }

        EventPayload<BroadcastEventPayload> payload = new()
        {
            Events =
            [
                new()
                {
                    DeviceBroadcastStatus = new()
                    {
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        BroadcastStatus = VideoEventType.BroadcastUnavailable,
                        DeviceId = deviceId,
                    },
                },
            ],
        };

        await _clientMessenger.SendTo("ChangeDevice", "videoHub", user.Id, payload);
    }
}
