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
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Api.Services.Video;
using NoMercy.Api.WebSockets;
using NoMercy.Authorization;
using NoMercy.Data.Activity;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Configuration;
using NoMercy.Setup.Cast;

namespace NoMercy.Api.Hubs;

public partial class VideoHub : ConnectionHub
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IClientMessenger _clientMessenger;
    private readonly VideoPlaybackService _videoPlaybackService;
    private readonly VideoPlayerStateManager _videoPlayerStateManager;
    private readonly VideoPlaylistManager _videoPlaylistManager;
    private readonly VideoPlaybackCommandHandler _commandHandler;
    private readonly CastSessionTokenService _castTokenService;
    private readonly DeviceBusRegistry _busRegistry;
    private readonly INetworkDiscovery? _networkDiscovery;
    private readonly IUserDataRepository _userDataRepository;

    private readonly IDbContextFactory<MediaContext> _contextFactory;

    private readonly IChromeCastService _chromeCast;

    private readonly ILogger<VideoHub> _logger;

    public VideoHub(
        ILogger<VideoHub> logger,
        IHttpContextAccessor httpContextAccessor,
        IDbContextFactory<MediaContext> contextFactory,
        ConnectedClients connectedClients,
        IClientMessenger clientMessenger,
        VideoPlaybackService videoPlaybackService,
        VideoPlayerStateManager videoPlayerStateManager,
        VideoPlaylistManager videoPlaylistManager,
        VideoPlaybackCommandHandler commandHandler,
        IActivityLogger activityLogger,
        CastSessionTokenService castTokenService,
        DeviceBusRegistry busRegistry,
        IChromeCastService chromeCast,
        IUserDataRepository userDataRepository,
        INetworkDiscovery? networkDiscovery = null
    )
        : base(httpContextAccessor, contextFactory, connectedClients, activityLogger)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _clientMessenger = clientMessenger;
        _contextFactory = contextFactory;
        _videoPlaybackService = videoPlaybackService;
        _videoPlayerStateManager = videoPlayerStateManager;
        _videoPlaylistManager = videoPlaylistManager;
        _commandHandler = commandHandler;
        _castTokenService = castTokenService;
        _busRegistry = busRegistry;
        _chromeCast = chromeCast;
        _networkDiscovery = networkDiscovery;
        _userDataRepository = userDataRepository;
    }

    private static readonly ConcurrentDictionary<Guid, Device> CurrentDevice = new();

    // ── Cast-receiver helpers (Phase 0) ──────────────────────────────────────

    private string ResolveServerUrl()
    {
        string? external = _networkDiscovery?.ExternalAddress;
        return string.IsNullOrEmpty(external)
            ? ExternalServicesConfig.Current.ApiBaseUrl
            : external;
    }

    private string ResolveSenderLocale()
    {
        string? header =
            _httpContextAccessor.HttpContext?.Request.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrEmpty(header))
            return "en-US";

        string first = header.Split(',')[0].Split(';')[0].Trim();
        return string.IsNullOrEmpty(first) ? "en-US" : first;
    }

    private CastIntent ResolveVideoIntent(Guid userId)
    {
        // If the user has a live video player state when handing off to the TV,
        // resume that exact item. Otherwise idle — receiver shows the splash.
        if (!_videoPlayerStateManager.TryGetValue(userId, out VideoPlayerState? state))
            return CastIntent.Idle();
        if (state.CurrentItem is null)
            return CastIntent.Idle();

        // CurrentList is "/{type}/{listId}/watch" — extract type for navigation.
        string path = state.CurrentList.ToString().TrimStart('/');
        string[] parts = path.Split('/');
        if (parts.Length < 2)
            return CastIntent.Idle();

        string mediaType = parts[0];
        string mediaId = state.CurrentItem.Id.ToString();
        int? resumeAt = state.Time > 0 ? state.Time / 1000 : null;
        // The language the handing-off device was playing travels with the
        // position, for the same reason the position does: the receiver opens
        // its own session and would otherwise start the episode over in whatever
        // the file declares.
        string? audioLanguage = state.CurrentAudio?.Language;
        return CastIntent.PlayVideo(mediaType, mediaId, resumeAt, audioLanguage);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user == null)
            return;

        bool stopPlayback = false;
        Ulid stoppedDeviceId = Ulid.Empty;
        Ulid stoppedMediaId = Ulid.Empty;
        string? stoppedTitle = null;

        if (ConnectedClients.Clients.TryGetValue(Context.ConnectionId, out Client? client))
            if (_videoPlayerStateManager.TryGetValue(user.Id, out VideoPlayerState? state))
                if (state.DeviceId == client.DeviceId)
                {
                    stopPlayback = true;
                    stoppedDeviceId = client.Id;
                    stoppedMediaId = state.CurrentItem?.VideoId ?? Ulid.Empty;
                    stoppedTitle = state.CurrentItem?.Title;
                }

        await base.OnDisconnectedAsync(exception);

        if (_videoPlayerStateManager.TryGetValue(user.Id, out VideoPlayerState? playerState))
        {
            List<Device> connectedDevices = Devices();

            if (connectedDevices.Count == 0)
            {
                playerState.DeviceId = null;
                playerState.PlayState = false;
                playerState.Actions = new()
                {
                    Disallows = new()
                    {
                        Previous = true,
                        Next = true,
                        Resuming = true,
                        Pausing = true,
                        Muting = true,
                        Seeking = true,
                        Stopping = true,
                    },
                };
            }
            else if (stopPlayback)
            {
                playerState.PlayState = false;
                playerState.Actions = new()
                {
                    Disallows = new()
                    {
                        Pausing = !playerState.PlayState,
                        Resuming = playerState.PlayState,
                        Stopping = true,
                        Seeking = true,
                        Muting = true,
                        Previous =
                            playerState.CurrentItem is null
                            || playerState.Playlist.IndexOf(playerState.CurrentItem) == 0,
                        Next =
                            playerState.CurrentItem is null
                            || playerState.Playlist.IndexOf(playerState.CurrentItem)
                                == playerState.Playlist.Count - 1,
                    },
                };
            }
        }

        await _videoPlaybackService.UpdatePlaybackState(user, playerState);

        if (stopPlayback && stoppedDeviceId != Ulid.Empty)
        {
            try
            {
                await ActivityLogger.LogPlaybackAsync(
                    "playback.stopped",
                    user.Id,
                    stoppedDeviceId,
                    stoppedMediaId,
                    new { media_type = "video", title = stoppedTitle }
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to log playback.stopped: {Message}", ex.Message);
            }
        }

        _logger.LogDebug("Video client disconnected");
    }
}
