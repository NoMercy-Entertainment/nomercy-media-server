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

using FlexLabs.EntityFrameworkCore.Upsert;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Media;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Http;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.NewtonSoftConverters;

namespace NoMercy.Api.Services.Video;

public class VideoPlaybackCommandHandler(
    VideoPlaybackService videoPlaybackService,
    IServiceScopeFactory scopeFactory,
    ILogger<VideoPlaybackCommandHandler> logger
)
{
    public async Task HandleCommand(
        User user,
        string command,
        object? data,
        VideoPlayerState state,
        Client? device
    )
    {
        switch (command)
        {
            case "play":
                HandlePlay(user, state);
                break;
            case "pause":
                HandlePause(user, state);
                break;
            case "seek":
                await HandleSeek(user, state, data);
                break;
            case "item":
                await HandleItem(state, data);
                break;
            case "episode":
                await HandleEpisode(state, data);
                break;
            case "forward":
                await HandleForward(user, state, data);
                break;
            case "backward":
                await HandleBackward(user, state, data);
                break;
            case "next":
                HandleNext(state);
                break;
            case "previous":
                HandlePrevious(state);
                break;
            case "nextChapter":
                HandleNextChapter(state);
                break;
            case "previousChapter":
                HandlePreviousChapter(state);
                break;
            case "stop":
                HandleStop(state);
                break;
            case "mute":
                state.Muted = !state.Muted;
                break;
            case "volume":
                await HandleVolume(data, state, device);
                break;
            case "audio":
                await HandleAudio(user, state, data);
                break;
            case "cycleAudio":
                await HandleCycleAudio(user, state);
                break;
            case "caption":
                await HandleCaption(user, state, data);
                break;
            case "cycleCaption":
                await HandleCycleCaption(user, state);
                break;
            case "quality":
                await HandleQuality(user, state, data);
                break;
            default:
                // Handle unknown command or log it
                logger.LogWarning("Unknown command: {Command}", command);
                break;
        }
    }

    private void HandlePlay(User user, VideoPlayerState state)
    {
        state.PlayState = true;
    }

    private void HandlePause(User user, VideoPlayerState state)
    {
        state.PlayState = false;
    }

    private async Task HandleSeek(User user, VideoPlayerState state, object? data)
    {
        if (!int.TryParse(data?.ToString() ?? "0", out int seconds))
            return;
        state.Time = seconds * 1000;
        await videoPlaybackService.StoreWatchProgression(state, user);
    }

    private async Task HandleForward(User user, VideoPlayerState state, object? data)
    {
        if (!int.TryParse(data?.ToString() ?? "10", out int seconds))
            return;
        state.Time += seconds * 1000;
        await videoPlaybackService.StoreWatchProgression(state, user);
    }

    private async Task HandleBackward(User user, VideoPlayerState state, object? data)
    {
        if (state.Time < 10)
        {
            state.Time = 0;
            return;
        }

        if (!int.TryParse(data?.ToString() ?? "10", out int seconds))
            return;
        state.Time -= seconds * 1000;
        await videoPlaybackService.StoreWatchProgression(state, user);
    }

    private void HandleNext(VideoPlayerState state)
    {
        if (state.CurrentItem == null)
            return;

        int currentIndex = state.Playlist.IndexOf(state.CurrentItem);
        if (currentIndex < state.Playlist.Count - 1)
        {
            state.CurrentItem = state.Playlist[currentIndex + 1];
            state.Time = 0;
        }
        else
        {
            HandlePlaylistCompletion(state);
        }
    }

    private void HandlePlaylistCompletion(VideoPlayerState state)
    {
        // If repeat is off, stop playback
        state.PlayState = false;
        state.Time = 0;
        state.CurrentItem = null;
    }

    private void HandlePrevious(VideoPlayerState state)
    {
        if (state.CurrentItem is null)
            return;

        if (state.Time >= 3000)
        {
            state.Time = 0;
            return;
        }

        if (state.Playlist.IndexOf(state.CurrentItem) == 0)
            return;

        int currentIndex = state.Playlist.IndexOf(state.CurrentItem);
        if (currentIndex > 0)
        {
            state.CurrentItem = state.Playlist[currentIndex - 1];
            state.Time = 0;
        }
    }

    private Task HandleItem(VideoPlayerState state, object? data)
    {
        if (data is null || state.CurrentItem is null)
            return Task.CompletedTask;

        if (!int.TryParse(data.ToString().OrEmpty(), out int itemId))
            return Task.CompletedTask;
        VideoPlaylistResponseDto? item = state.Playlist.ElementAtOrDefault(itemId);

        if (item is null)
            return Task.CompletedTask;

        state.CurrentItem = item;
        state.Time = 0;

        return Task.CompletedTask;
    }

    private class EpisodeData
    {
        [JsonProperty("season")]
        public int Season { get; set; }

        [JsonProperty("episode")]
        public int Episode { get; set; }
    }

    private async Task HandleEpisode(VideoPlayerState state, object? data)
    {
        if (data is null || state.CurrentItem is null)
            return;

        EpisodeData? episodeData = data.ToString().FromJson<EpisodeData>();
        if (episodeData is null || episodeData.Season == 0 || episodeData.Episode == 0)
            return;

        VideoPlaylistResponseDto? item = state.Playlist.FirstOrDefault(p =>
            p.PlaylistType == MediaTypes.TvMediaType
            && p.Season == episodeData.Season
            && p.Episode == episodeData.Episode
        );

        if (item is null)
            return;

        state.CurrentItem = item;
        state.Time = 0;
        state.PlayState = true;
    }

    private void HandleStop(VideoPlayerState state)
    {
        state.DeviceId = null;
        state.CurrentItem = null;
        state.PlayState = false;
        state.Time = 0;
        state.Playlist = [];
        state.CurrentList = new("", UriKind.Relative);
        state.Actions = new()
        {
            Disallows = new()
            {
                Previous = true,
                Next = true,
                Resuming = true,
                Pausing = true,
                Stopping = true,
                Seeking = true,
                Muting = true,
            },
        };
    }

    private async Task HandleVolume(object? data, VideoPlayerState state, Client? device)
    {
        if (data is null || state.CurrentItem is null)
            return;

        if (!int.TryParse(data.ToString().OrEmpty(), out int volume))
            return;
        volume = Math.Clamp(volume, 0, 100);

        state.VolumePercentage = volume;
        state.Muted = false;

        if (device is not null)
        {
            device.VolumePercent = volume;

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IDbContextFactory<MediaContext> contextFactory =
                scope.ServiceProvider.GetRequiredService<IDbContextFactory<MediaContext>>();
            await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync();
            await mediaContext
                .Devices.Where(d => d.DeviceId == device.DeviceId)
                .ExecuteUpdateAsync(d => d.SetProperty(x => x.VolumePercent, volume));
        }
    }

    private void HandlePreviousChapter(VideoPlayerState state)
    {
        if (state.CurrentItem is null)
            return;
        IChapter? currentChapter = state.Chapters.FirstOrDefault(c =>
            state.Time >= c.StartTime && state.Time <= c.EndTime
        );
        if (currentChapter is null)
            return;

        if (state.Time - 3000 > currentChapter.StartTime)
        {
            state.Time = currentChapter.StartTime;
            return;
        }

        int index = state.Chapters.IndexOf(currentChapter);
        if (index > 0)
        {
            IChapter previousChapter = state.Chapters[index - 1];
            state.Time = previousChapter.StartTime;
        }
    }

    private void HandleNextChapter(VideoPlayerState state)
    {
        if (state.CurrentItem is null)
            return;

        IChapter? currentChapter = state.Chapters.FirstOrDefault(c =>
            state.Time >= c.StartTime && state.Time <= c.EndTime
        );
        if (currentChapter is null)
            return;

        int index = state.Chapters.IndexOf(currentChapter);
        if (index + 1 <= state.Chapters.Count - 1)
        {
            IChapter nextChapter = state.Chapters[index + 1];
            state.Time = nextChapter.StartTime;
        }
    }

    private async Task HandleAudio(User user, VideoPlayerState state, object? data)
    {
        if (data is null || state.CurrentItem is null)
            return;

        if (!int.TryParse(data.ToString().OrEmpty(), out int index))
            return;

        if (index < 0)
        {
            state.CurrentAudio = null;
            await SetPlaybackPreference(
                user,
                state,
                state.CurrentAudio,
                state.CurrentQuality,
                state.CurrentCaption
            );
            return;
        }

        IAudio? audio = state.Audio.ElementAtOrDefault(index);
        if (audio is not null)
        {
            state.CurrentAudio = audio;
        }

        await SetPlaybackPreference(
            user,
            state,
            state.CurrentAudio,
            state.CurrentQuality,
            state.CurrentCaption
        );
    }

    private async Task HandleCycleAudio(User user, VideoPlayerState state)
    {
        if (state.CurrentItem is null)
            return;

        int currentIndex = state.CurrentAudio is not null
            ? state.Audio.IndexOf(state.CurrentAudio)
            : -1;
        if (currentIndex >= state.Audio.Count - 1)
        {
            state.CurrentAudio = state.Audio.First();
            await SetPlaybackPreference(
                user,
                state,
                state.CurrentAudio,
                state.CurrentQuality,
                state.CurrentCaption
            );
            return;
        }

        IAudio nextAudio = state.Audio[currentIndex + 1];
        state.CurrentAudio = nextAudio;

        await SetPlaybackPreference(
            user,
            state,
            state.CurrentAudio,
            state.CurrentQuality,
            state.CurrentCaption
        );
    }

    private async Task HandleCaption(User user, VideoPlayerState state, object? data)
    {
        if (data is null)
            return;

        if (!int.TryParse(data.ToString().OrEmpty(), out int index))
            return;

        if (index < 0)
        {
            state.CurrentCaption = null;
            await SetPlaybackPreference(
                user,
                state,
                state.CurrentAudio,
                state.CurrentQuality,
                state.CurrentCaption
            );
            return;
        }

        ISubtitle? track = state.Captions.ElementAtOrDefault(index);
        if (track is not null)
        {
            state.CurrentCaption = track;
        }

        await SetPlaybackPreference(
            user,
            state,
            state.CurrentAudio,
            state.CurrentQuality,
            state.CurrentCaption
        );
    }

    private async Task HandleCycleCaption(User user, VideoPlayerState state)
    {
        if (state.CurrentItem is null)
            return;

        int currentIndex = state.CurrentCaption is not null
            ? state.Captions.IndexOf(state.CurrentCaption)
            : -1;
        if (currentIndex >= state.Captions.Count - 1)
        {
            state.CurrentCaption = null;
            await SetPlaybackPreference(
                user,
                state,
                state.CurrentAudio,
                state.CurrentQuality,
                null
            );
            return;
        }
        if (currentIndex < 0)
        {
            state.CurrentCaption = state.Captions.First();
            await SetPlaybackPreference(
                user,
                state,
                state.CurrentAudio,
                state.CurrentQuality,
                state.CurrentCaption
            );
            return;
        }

        ISubtitle nextCaption = state.Captions[currentIndex + 1];
        state.CurrentCaption = nextCaption;

        await SetPlaybackPreference(
            user,
            state,
            state.CurrentAudio,
            state.CurrentQuality,
            state.CurrentCaption
        );
    }

    private async Task HandleQuality(User user, VideoPlayerState state, object? data)
    {
        if (data is null)
            return;

        if (!int.TryParse(data.ToString().OrEmpty(), out int index))
            return;

        if (index < 0)
        {
            state.CurrentQuality = null;
            await SetPlaybackPreference(
                user,
                state,
                state.CurrentAudio,
                null,
                state.CurrentCaption
            );
            return;
        }

        IVideo? video = state.Qualities.ElementAtOrDefault(index);
        if (video is not null)
        {
            state.CurrentQuality = video;
        }

        await SetPlaybackPreference(user, state, state.CurrentAudio, video, state.CurrentCaption);
    }

    private async Task UserSetLibraryPreference(
        MediaContext mediaContext,
        User user,
        VideoPlayerState state
    )
    {
        if (state.CurrentItem is null)
            return;

        bool userHasLibraryPreference = await mediaContext
            .Users.Include(u => u.PlaybackPreferences)
                .ThenInclude(playbackPreference => playbackPreference.Library)
            .Where(u => u.Id == user.Id)
            .Select(x =>
                x.PlaybackPreferences.Any(p =>
                    p.Library != null && p.Library.Type == state.CurrentItem!.LibraryType
                )
            )
            .FirstAsync();

        if (userHasLibraryPreference)
            return;

        PlaybackPreference playbackPreference = new()
        {
            UserId = user.Id,
            Video = state.CurrentQuality?.Width is not null
                ? new()
                {
                    Width = state.CurrentQuality.Width,
                    BitRate = null,
                    FileSize = null,
                    Height = null,
                }
                : null,
            Audio = state.CurrentAudio?.Language is not null
                ? new() { Language = state.CurrentAudio.Language, FileSize = null }
                : null,
            Subtitle = state.CurrentCaption?.Language is not null
                ? new()
                {
                    Language = state.CurrentCaption.Language,
                    Type = state.CurrentCaption.Type,
                    Codec = state.CurrentCaption.Codec,
                    FileSize = null,
                }
                : null,
            LibraryId = mediaContext
                .Libraries.Where(l => l.Type == state.CurrentItem!.LibraryType)
                .Select(l => l.Id)
                .FirstOrDefault(),
        };

        await mediaContext
            .PlaybackPreferences.Upsert(playbackPreference)
            .On(p => new { p.UserId, p.LibraryId })
            .WhenMatched(
                (po, pi) =>
                    new()
                    {
                        LibraryId = pi.LibraryId,
                        _audio = pi._audio,
                        _video = pi._video,
                        _subtitle = pi._subtitle,
                    }
            )
            .RunAsync();
    }

    private async Task SetPlaybackPreference(
        User user,
        VideoPlayerState state,
        IAudio? audio,
        IVideo? video,
        ISubtitle? subtitle
    )
    {
        if (state.CurrentItem is null)
            return;

        PlaybackPreference playbackPreference = new()
        {
            UserId = user.Id,
            MovieId =
                state.CurrentItem.PlaylistType == MediaTypes.MovieMediaType
                    ? state.CurrentItem.TmdbId
                    : null,
            TvId = state.CurrentItem.PlaylistType
                is MediaTypes.TvMediaType
                    or MediaTypes.AnimeMediaType
                ? state.CurrentItem.TmdbId
                : null,
            CollectionId =
                state.CurrentItem.PlaylistType == MediaTypes.CollectionMediaType
                    ? int.Parse(state.CurrentItem.PlaylistId)
                    : null,
            SpecialId =
                state.CurrentItem.PlaylistType == MediaTypes.SpecialMediaType
                    ? Ulid.Parse(state.CurrentItem.PlaylistId)
                    : null,
            Video = video?.Width is not null
                ? new()
                {
                    Width = video.Width,
                    BitRate = null,
                    FileSize = null,
                    Height = null,
                }
                : null,
            Audio = audio?.Language is not null
                ? new() { Language = audio.Language, FileSize = null }
                : null,
            Subtitle = subtitle?.Language is not null
                ? new()
                {
                    Language = subtitle.Language,
                    Type = subtitle.Type,
                    Codec = subtitle.Codec,
                    FileSize = null,
                }
                : null,
        };

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IDbContextFactory<MediaContext> contextFactory = scope.ServiceProvider.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync();

        UpsertCommandBuilder<PlaybackPreference> query = mediaContext.PlaybackPreferences.Upsert(
            playbackPreference
        );

        switch (state.CurrentItem.PlaylistType)
        {
            case MediaTypes.MovieMediaType:
                query.On(p => new { p.UserId, p.MovieId });
                break;
            case MediaTypes.TvMediaType:
            case MediaTypes.AnimeMediaType:
                query.On(p => new { p.UserId, p.TvId });
                break;
            case MediaTypes.CollectionMediaType:
                query.On(p => new { p.UserId, p.CollectionId });
                break;
            case MediaTypes.SpecialMediaType:
                query.On(p => new { p.UserId, p.SpecialId });
                break;
        }

        await query
            .WhenMatched(
                (po, pi) =>
                    new()
                    {
                        _audio = pi._audio,
                        _video = pi._video,
                        _subtitle = pi._subtitle,
                    }
            )
            .RunAsync();

        await UserSetLibraryPreference(mediaContext, user, state);
    }
}
