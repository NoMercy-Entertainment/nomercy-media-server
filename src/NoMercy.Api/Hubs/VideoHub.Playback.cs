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
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Hubs.Shared;
using NoMercy.Api.Services.Video;
using NoMercy.Authorization;
using NoMercy.Database.Models.Media;
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
    public async Task<HubCommandResult> SetTime(VideoProgressRequest request)
    {
        HubCommandResult? error = ValidateProgressRequest(request);
        if (error is not null)
            return error;
        if (request.VideoId == Ulid.Empty)
            return HubCommandResult.Invalid("Video id is required.");
        if (UserCacheService.GetUser(Context.User.UserId()) is null)
            return HubCommandResult.Forbidden("Caller is not available.");

        try
        {
            if (!await _videoFileRepository.ExistsAsync(request.VideoId))
                return HubCommandResult.NotFound("Video was not found.");
            await SetTimeCoreAsync(request);
            return HubCommandResult.Success();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not set video time");
            return HubCommandResult.Failed();
        }
    }

    private async Task SetTimeCoreAsync(VideoProgressRequest request)
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

    public async Task<HubCommandResult> RemoveWatched(VideoProgressRequest request)
    {
        HubCommandResult? error = ValidateProgressRequest(request);
        if (error is not null)
            return error;

        try
        {
            int removed = await RemoveWatchedCoreAsync(request);
            return removed > 0
                ? HubCommandResult.Success()
                : HubCommandResult.NotFound("Watched item was not found.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not remove watched item");
            return HubCommandResult.Failed();
        }
    }

    private async Task<int> RemoveWatchedCoreAsync(VideoProgressRequest request)
    {
        string? guid = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(guid, out Guid userId))
            return 0;

        User? user = UserCacheService.Users.FirstOrDefault(x => x.Id.Equals(userId));

        if (user is null)
            return 0;

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

        return await _userDataRepository.RemoveForItemAsync(
            user.Id,
            request.PlaylistType,
            intId,
            ulidId
        );
    }

    public Task<HubCommandResult> StartPlaybackCommand(string? type, dynamic? listId, int? itemId)
    {
        if (
            string.IsNullOrWhiteSpace(type)
            || type
                is not (
                    MediaTypes.MovieMediaType
                    or MediaTypes.TvMediaType
                    or MediaTypes.CollectionMediaType
                    or MediaTypes.SpecialMediaType
                )
            || listId is null
            || itemId is <= 0
        )
            return Task.FromResult(
                HubCommandResult.Invalid("Playback type or item id is invalid.")
            );

        return HubCommandResult.ExecuteAsync(
            () => StartPlaybackCoreAsync(type, listId, itemId),
            _logger
        );
    }

    private async Task StartPlaybackCoreAsync(string? type, dynamic? listId, int? itemId)
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
        VideoPlaylistResponseDto? playbackItem = null;

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
            playbackItem = playlistResult.Item1;

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
            _logger.LogInformation(
                "Invalid playlist type for {Title} ({ItemId}): {Message}",
                playbackItem?.Title,
                playbackItem?.Id ?? itemId,
                ex.Message
            );

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
                        message: $"Playback start failed for {playbackItem?.Title ?? "<unknown>"} (item {playbackItem?.Id ?? itemId}): {ex.Message}"
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
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error in StartPlaybackCommand for {Title} ({ItemId})",
                playbackItem?.Title,
                playbackItem?.Id ?? itemId
            );

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
                        message: $"Playback start failed for {playbackItem?.Title ?? "<unknown>"} (item {playbackItem?.Id ?? itemId}): {ex.Message}"
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
            throw;
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
        // Cast/remote-control needs the current item's structured chapter/audio/
        // caption/quality lists, which live on Metadata, not the slim wire DTO.
        Metadata? metadata = await _videoFileRepository.GetMetadataAsync(item.VideoId);
        User? userPreference = await _userDataRepository.GetWithPlaybackPreferencesAsync(user.Id);
        VideoPlayerState videoPlayerState = VideoPlayerStateFactory.Create(
            userPreference,
            metadata,
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

    public Task<HubCommandResult> PlaybackCommand(string? command, object? data = null)
    {
        if (
            string.IsNullOrWhiteSpace(command)
            || !new[]
            {
                "play",
                "pause",
                "seek",
                "item",
                "episode",
                "forward",
                "backward",
                "next",
                "previous",
                "nextChapter",
                "previousChapter",
                "stop",
                "mute",
                "volume",
                "audio",
                "cycleAudio",
                "caption",
                "cycleCaption",
                "quality",
            }.Contains(command, StringComparer.Ordinal)
        )
            return Task.FromResult(HubCommandResult.Invalid("Unknown playback command."));

        HubCommandResult? dataError = ValidatePlaybackData(command, data);
        if (dataError is not null)
            return Task.FromResult(dataError);

        return HubCommandResult.ExecuteAsync(() => PlaybackCoreAsync(command, data), _logger);
    }

    private async Task PlaybackCoreAsync(string? command, object? data = null)
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

    public async Task<HubCommandResult> ChangeDeviceCommand(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return HubCommandResult.Invalid("Device id is required.");

        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return HubCommandResult.Forbidden("Caller is not available.");

        try
        {
            (List<Device> connectedDevices, _) = await _busRegistry.WithOwnedTvsAsync(
                user.Id,
                Devices()
            );
            if (
                !connectedDevices.Any(device =>
                    device.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
                )
            )
                return HubCommandResult.NotFound("Device was not found for this user.");

            await ChangeDeviceCoreAsync(deviceId, connectedDevices);
            return HubCommandResult.Success();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not change video device");
            return HubCommandResult.Failed();
        }
    }

    private async Task ChangeDeviceCoreAsync(string? deviceId, List<Device> connectedDevices)
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

        EventPayload<BroadcastEventPayload<VideoEventType>> payload = new()
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

    private static HubCommandResult? ValidateProgressRequest(VideoProgressRequest? request)
    {
        if (request is null)
            return HubCommandResult.Invalid("Progress request is required.");
        if (
            request.PlaylistType
                is not (
                    MediaTypes.MovieMediaType
                    or MediaTypes.TvMediaType
                    or MediaTypes.CollectionMediaType
                    or MediaTypes.SpecialMediaType
                )
            || request.Time < 0
            || request.Time > int.MaxValue / 1000
        )
            return HubCommandResult.Invalid("Progress request is out of range.");
        if (request.PlaylistType is not MediaTypes.SpecialMediaType && request.TmdbId <= 0)
            return HubCommandResult.Invalid("Media id is required.");
        if (
            request.PlaylistType is MediaTypes.SpecialMediaType
            && request.SpecialId is null
            && request.TmdbId <= 0
        )
            return HubCommandResult.Invalid("Special id is required.");
        return null;
    }

    private static HubCommandResult? ValidatePlaybackData(string command, object? data)
    {
        if (
            command
            is not (
                "seek"
                or "forward"
                or "backward"
                or "volume"
                or "item"
                or "audio"
                or "caption"
                or "quality"
            )
        )
            return null;

        string? raw = data?.ToString();
        if (raw is null && command is "forward" or "backward")
            raw = "10";
        if (!int.TryParse(raw, out int value))
            return HubCommandResult.Invalid("Playback data must be an integer.");

        if (
            command is "volume" && value is < 0 or > 100
            || command is "seek" or "forward" or "backward" && value is < 0 or > int.MaxValue / 1000
            || command is "item" && value < 0
            || command is "audio" or "caption" or "quality" && value < -1
        )
            return HubCommandResult.Invalid("Playback data is out of range.");
        return null;
    }
}
