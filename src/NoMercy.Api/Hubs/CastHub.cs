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

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Authorization;
using NoMercy.Data.Activity;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Auth;
using Sharpcaster.Models.ChromecastStatus;
using Sharpcaster.Models.Media;

namespace NoMercy.Api.Hubs;

public class CastHub : ConnectionHub
{
    private readonly IClientMessenger _clientMessenger;

    private readonly IAuthTokenStore _authTokenStore;

    private readonly IChromeCastService _chromeCast;

    private readonly ILogger<CastHub> _logger;

    public CastHub(
        ILogger<CastHub> logger,
        IHttpContextAccessor httpContextAccessor,
        IDbContextFactory<MediaContext> contextFactory,
        ConnectedClients connectedClients,
        IClientMessenger clientMessenger,
        IActivityLogger activityLogger,
        IAuthTokenStore authTokenStore,
        IChromeCastService chromeCast
    )
        : base(httpContextAccessor, contextFactory, connectedClients, activityLogger)
    {
        _logger = logger;
        _authTokenStore = authTokenStore;
        _clientMessenger = clientMessenger;
        _chromeCast = chromeCast;
    }

    public class TimeData
    {
        [JsonProperty("currentTime")]
        public double CurrentTime { get; set; }

        [JsonProperty("duration")]
        public double Duration { get; set; }

        [JsonProperty("percentage")]
        public double Percentage { get; set; }

        [JsonProperty("remaining")]
        public double Remaining { get; set; }

        [JsonProperty("currentTimeHuman")]
        public string CurrentTimeHuman { get; set; } = string.Empty;

        [JsonProperty("durationHuman")]
        public string DurationHuman { get; set; } = string.Empty;

        [JsonProperty("remainingHuman")]
        public string RemainingHuman { get; set; } = string.Empty;
    }

    public class TextTrack
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("default")]
        public bool Default { get; set; }

        [JsonProperty("file")]
        public string File { get; set; } = string.Empty;

        [JsonProperty("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonProperty("label")]
        public string? Label { get; set; }

        [JsonProperty("language")]
        public string? Language { get; set; }

        [JsonProperty("type")]
        public string? Type { get; set; }

        [JsonProperty("ext")]
        public string? Ext { get; set; }
    }

    public class AudioTrack
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("language")]
        public string Language { get; set; } = string.Empty;

        [JsonProperty("label")]
        public string Label { get; set; } = string.Empty;
    }

    public class PlaylistItem
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonProperty("seasonName")]
        public string SeasonName { get; set; } = string.Empty;

        [JsonProperty("progress")]
        public ProgressDto Progress { get; set; } = new();

        [JsonProperty("duration")]
        public string Duration { get; set; } = string.Empty;

        [JsonProperty("file")]
        public string File { get; set; } = string.Empty;

        [JsonProperty("image")]
        public string Image { get; set; } = string.Empty;

        [JsonProperty("title")]
        public string Title { get; set; } = string.Empty;

        [JsonProperty("tracks")]
        public TextTrack[] Tracks { get; set; } = [];

        [JsonProperty("withCredentials")]
        public bool WithCredentials { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("season")]
        public int Season { get; set; }

        [JsonProperty("episode")]
        public int Episode { get; set; }

        [JsonProperty("show")]
        public string Show { get; set; } = string.Empty;

        [JsonProperty("year")]
        public int Year { get; set; }

        [JsonProperty("logo")]
        public string Logo { get; set; } = string.Empty;

        [JsonProperty("rating")]
        public RatingDto Rating { get; set; } = new();
    }

    public class CastPlayerState
    {
        [JsonProperty("time")]
        public TimeData TimeData { get; set; } = new();

        [JsonProperty("volume")]
        public int Volume { get; set; }

        [JsonProperty("muted")]
        public bool Muted { get; set; }

        [JsonProperty("isPlaying")]
        public bool IsPlaying { get; set; }

        [JsonProperty("playlist")]
        public PlaylistItem[] Playlist { get; set; } = [];

        [JsonProperty("currentPlaylistItem")]
        public PlaylistItem? CurrentPlaylistItem { get; set; }

        [JsonProperty("subtitles")]
        public TextTrack[] Subtitles { get; set; } = [];

        [JsonProperty("currentSubtitleTrack")]
        public TextTrack CurrentSubtitleTextTrack { get; set; } = new();

        [JsonProperty("audioTracks")]
        public AudioTrack[] AudioTracks { get; set; } = [];

        [JsonProperty("currentAudioTrack")]
        public int CurrentAudioTrack { get; set; }
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        _logger.LogInformation("Cast client connected");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
        _logger.LogInformation("Cast client disconnected");
    }

    public string[] GetChromeCasts()
    {
        return _chromeCast.GetChromeCasts();
    }

    public async Task SelectChromecast(string name)
    {
        await _chromeCast.SelectChromecast(name);
    }

    public async Task Launch()
    {
        await _chromeCast.Launch();
    }

    public async Task CastPlaylist(string value)
    {
        await _chromeCast.CastPlaylist(value, accessToken: _authTokenStore.AccessToken);
    }

    public ChromecastStatus? GetChromecastStatus()
    {
        return _chromeCast.GetChromecastStatus();
    }

    public MediaStatus? GetMediaStatus()
    {
        return _chromeCast.GetMediaStatus();
    }

    public async Task Stop()
    {
        await _chromeCast.Stop();
    }

    public async Task Disconnect()
    {
        await _chromeCast.Disconnect();
    }

    public Task Play() => RelayToCaller("Play");

    public Task Pause() => RelayToCaller("Pause");

    public Task Time(TimeData time) => RelayToCaller("Time", time);

    public Task Ended() => RelayToCaller("Ended");

    public Task Volume(int volume) => RelayToCaller("Volume", volume);

    public Task Muted(bool muted) => RelayToCaller("Muted", muted);

    public Task Item(PlaylistItem item) => RelayToCaller("Item", item);

    public Task Playlist(PlaylistItem[] item) => RelayToCaller("Playlist", item);

    public Task SubtitleTracks(TextTrack[] subtitleTracks) =>
        RelayToCaller("SubtitleTracks", subtitleTracks);

    public Task CurrentSubtitleTrack(TextTrack subtitleTrack) =>
        RelayToCaller("CurrentSubtitleTrack", subtitleTrack);

    public Task AudioTracks(AudioTrack[] audioTrack) => RelayToCaller("AudioTracks", audioTrack);

    public Task CurrentAudioTrack(AudioTrack audioTrack) =>
        RelayToCaller("CurrentAudioTrack", audioTrack);

    public Task GetPlayerState() => RelayToCaller("GetPlayerState");

    public Task PlayerState(CastPlayerState state) => RelayToCaller("MusicPlayerState", state);

    public Task SetAudioTrack(int audioTrack) => RelayToCaller("SetAudioTrack", audioTrack);

    public Task SetSubtitleTrack(int subtitleTrack) =>
        RelayToCaller("SetSubtitleTrack", subtitleTrack);

    public Task SetPlaylistItem(int item) => RelayToCaller("SetPlaylistItem", item);

    public Task SetVolume(int volume) => RelayToCaller("SetVolume", volume);

    public Task SetMuted(bool muted) => RelayToCaller("SetMuted", muted);

    public Task SetSeek(int time) => RelayToCaller("SetSeek", time);

    public Task SetNext() => RelayToCaller("SetNext");

    public Task SetPrevious() => RelayToCaller("SetPrevious");

    public Task SetPlay() => RelayToCaller("SetPlay");

    public Task SetPause() => RelayToCaller("SetPause");

    public Task SetStop() => RelayToCaller("SetStop");

    /// <summary>Relays a cast event to the calling user's other castHub connections.</summary>
    private async Task RelayToCaller(string eventName, object? data = null)
    {
        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return;
        await _clientMessenger.SendTo(eventName, "castHub", user.Id, data);
    }
}
