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

    public async Task<HubCommandResult> SelectChromecast(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return HubCommandResult.Invalid("Chromecast name is required.");
        try
        {
            if (!_chromeCast.GetChromeCasts().Contains(name, StringComparer.OrdinalIgnoreCase))
                return HubCommandResult.NotFound("Chromecast was not found.");
            await _chromeCast.SelectChromecast(name);
            return HubCommandResult.Success();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not select Chromecast");
            return HubCommandResult.Failed();
        }
    }

    public Task<HubCommandResult> Launch() =>
        HubCommandResult.ExecuteAsync(() => _chromeCast.Launch(), _logger);

    public Task<HubCommandResult> CastPlaylist(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Task.FromResult(HubCommandResult.Invalid("Playlist is required."))
            : HubCommandResult.ExecuteAsync(
                () => _chromeCast.CastPlaylist(value, accessToken: _authTokenStore.AccessToken),
                _logger
            );

    public ChromecastStatus? GetChromecastStatus()
    {
        return _chromeCast.GetChromecastStatus();
    }

    public MediaStatus? GetMediaStatus()
    {
        return _chromeCast.GetMediaStatus();
    }

    public Task<HubCommandResult> Stop() =>
        HubCommandResult.ExecuteAsync(() => _chromeCast.Stop(), _logger);

    public Task<HubCommandResult> Disconnect() =>
        HubCommandResult.ExecuteAsync(() => _chromeCast.Disconnect(), _logger);

    public Task<HubCommandResult> Play() => RelayToCaller("Play");

    public Task<HubCommandResult> Pause() => RelayToCaller("Pause");

    public Task<HubCommandResult> Time(TimeData time) => RelayToCaller("Time", time);

    public Task<HubCommandResult> Ended() => RelayToCaller("Ended");

    public Task<HubCommandResult> Volume(int volume) => RelayToCaller("Volume", volume);

    public Task<HubCommandResult> Muted(bool muted) => RelayToCaller("Muted", muted);

    public Task<HubCommandResult> Item(PlaylistItem item) => RelayToCaller("Item", item);

    public Task<HubCommandResult> Playlist(PlaylistItem[] item) => RelayToCaller("Playlist", item);

    public Task<HubCommandResult> SubtitleTracks(TextTrack[] subtitleTracks) =>
        RelayToCaller("SubtitleTracks", subtitleTracks);

    public Task<HubCommandResult> CurrentSubtitleTrack(TextTrack subtitleTrack) =>
        RelayToCaller("CurrentSubtitleTrack", subtitleTrack);

    public Task<HubCommandResult> AudioTracks(AudioTrack[] audioTrack) =>
        RelayToCaller("AudioTracks", audioTrack);

    public Task<HubCommandResult> CurrentAudioTrack(AudioTrack audioTrack) =>
        RelayToCaller("CurrentAudioTrack", audioTrack);

    public Task<HubCommandResult> GetPlayerState() => RelayToCaller("GetPlayerState");

    public Task<HubCommandResult> PlayerState(CastPlayerState state) =>
        RelayToCaller("MusicPlayerState", state);

    public Task<HubCommandResult> SetAudioTrack(int audioTrack) =>
        RelayToCaller("SetAudioTrack", audioTrack);

    public Task<HubCommandResult> SetSubtitleTrack(int subtitleTrack) =>
        RelayToCaller("SetSubtitleTrack", subtitleTrack);

    public Task<HubCommandResult> SetPlaylistItem(int item) =>
        RelayToCaller("SetPlaylistItem", item);

    public Task<HubCommandResult> SetVolume(int volume) => RelayToCaller("SetVolume", volume);

    public Task<HubCommandResult> SetMuted(bool muted) => RelayToCaller("SetMuted", muted);

    public Task<HubCommandResult> SetSeek(int time) => RelayToCaller("SetSeek", time);

    public Task<HubCommandResult> SetNext() => RelayToCaller("SetNext");

    public Task<HubCommandResult> SetPrevious() => RelayToCaller("SetPrevious");

    public Task<HubCommandResult> SetPlay() => RelayToCaller("SetPlay");

    public Task<HubCommandResult> SetPause() => RelayToCaller("SetPause");

    public Task<HubCommandResult> SetStop() => RelayToCaller("SetStop");

    /// <summary>Relays a cast event to the calling user's other castHub connections.</summary>
    private async Task<HubCommandResult> RelayToCaller(string eventName, object? data = null)
    {
        HubCommandResult? error = ValidateRelay(eventName, data);
        if (error is not null)
            return error;

        User? user = UserCacheService.GetUser(Context.User.UserId());
        if (user is null)
            return HubCommandResult.Forbidden("Caller is not available.");

        return await HubCommandResult.ExecuteAsync(
            () => _clientMessenger.SendTo(eventName, "castHub", user.Id, data),
            _logger
        );
    }

    private static HubCommandResult? ValidateRelay(string eventName, object? data) =>
        eventName switch
        {
            "Time"
                when data is not TimeData time
                    || !double.IsFinite(time.CurrentTime)
                    || time.CurrentTime < 0
                    || !double.IsFinite(time.Duration)
                    || time.Duration < 0
                    || !double.IsFinite(time.Percentage)
                    || time.Percentage is < 0 or > 100
                    || !double.IsFinite(time.Remaining)
                    || time.Remaining < 0 => HubCommandResult.Invalid("Time is out of range."),
            "Volume" or "SetVolume" when data is int volume && volume is < 0 or > 100 =>
                HubCommandResult.Invalid("Volume must be between 0 and 100."),
            "Item" when data is not PlaylistItem item || string.IsNullOrWhiteSpace(item.Id) =>
                HubCommandResult.Invalid("Playlist item id is required."),
            "Playlist"
                when data is not PlaylistItem[] items
                    || items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id)) =>
                HubCommandResult.Invalid("Playlist items need ids."),
            "SubtitleTracks" when data is not TextTrack[] => HubCommandResult.Invalid(
                "Subtitle tracks are required."
            ),
            "AudioTracks" when data is not AudioTrack[] => HubCommandResult.Invalid(
                "Audio tracks are required."
            ),
            "CurrentSubtitleTrack" when data is not TextTrack => HubCommandResult.Invalid(
                "Subtitle track is required."
            ),
            "CurrentAudioTrack" when data is not AudioTrack => HubCommandResult.Invalid(
                "Audio track is required."
            ),
            "MusicPlayerState" when data is not CastPlayerState => HubCommandResult.Invalid(
                "Player state is required."
            ),
            "SetAudioTrack" or "SetPlaylistItem" or "SetSeek" when data is int value && value < 0 =>
                HubCommandResult.Invalid("Value must be non-negative."),
            "SetSubtitleTrack" when data is int subtitleTrack && subtitleTrack < -1 =>
                HubCommandResult.Invalid("Subtitle track is out of range."),
            _ => null,
        };
}
