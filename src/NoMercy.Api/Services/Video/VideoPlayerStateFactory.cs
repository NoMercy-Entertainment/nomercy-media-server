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

using NoMercy.Api.DTOs.Media;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using static System.Int32;

namespace NoMercy.Api.Services.Video;

public class VideoPlayerStateFactory
{
    /// <param name="userPreference">The user loaded with their playback preferences; null when the user row is gone.</param>
    /// <param name="metadata">The current item's probed chapters and tracks.</param>
    public static VideoPlayerState Create(
        User? userPreference,
        Metadata? metadata,
        Device device,
        VideoPlaylistResponseDto item,
        List<VideoPlaylistResponseDto> playlist,
        string type,
        dynamic listId
    )
    {
        ArgumentNullException.ThrowIfNull(listId);

        string id = listId.ToString();

        // parse id once and safely
        TryParse(id, out int parsedId);

        List<IChapter> chapters = metadata?.Chapters ?? [];
        List<IAudio> audioTracks = metadata?.Audio ?? [];
        List<ISubtitle> captions = metadata?.Subtitles ?? [];
        List<IVideo> qualities = metadata?.Video ?? [];

        // A user that could not be loaded plays with no track choice at all; a loaded
        // user without a matching preference gets the first quality, audio and caption.
        PlaybackPreference? playbackPreference = null;
        if (userPreference is not null)
            playbackPreference =
                FindPlaybackPreference(userPreference, id, parsedId, type)
                ?? CreateDefaultPlaybackPreference(qualities, audioTracks, captions);

        int index = playlist.IndexOf(item);

        return new()
        {
            DeviceId = device.DeviceId,
            VolumePercentage = device.VolumePercent ?? Device.DefaultVolumePercent,
            CurrentItem = item,
            CurrentAudio = playbackPreference?.Audio,
            CurrentCaption = playbackPreference?.Subtitle,
            CurrentQuality = playbackPreference?.Video,
            Chapters = chapters,
            Audio = audioTracks,
            Captions = captions,
            Qualities = qualities,
            Playlist = playlist,
            PlayState = true,
            Time = (item.Progress?.Time ?? 0) * 1000,
            Duration = item.Duration.ToMilliSeconds(),
            CurrentList = new($"/{type}/{listId}/watch", UriKind.Relative),
            Actions = new()
            {
                Disallows = new()
                {
                    Stopping = false,
                    Seeking = false,
                    Muting = false,
                    Pausing = false,
                    Resuming = true,
                    Previous = index == 0,
                    Next = index == playlist.Count - 1,
                },
            },
        };
    }

    private static PlaybackPreference? FindPlaybackPreference(
        User userPreference,
        string id,
        int parsedId,
        string type
    )
    {
        PlaybackPreference? byIds = userPreference.PlaybackPreferences.FirstOrDefault(p =>
            (
                p.MovieId is not null
                && p.MovieId.ToString() == id
                && MediaTypes.MovieMediaType == type
            )
            || (p.TvId is not null && p.TvId.ToString() == id && MediaTypes.TvMediaType == type)
            || (
                p.CollectionId is not null
                && p.CollectionId.ToString() == id
                && MediaTypes.CollectionMediaType == type
            )
            || (
                p.SpecialId is not null
                && p.SpecialId.ToString() == id
                && MediaTypes.SpecialMediaType == type
            )
        );

        if (byIds is not null)
            return byIds;

        return userPreference.PlaybackPreferences.FirstOrDefault(p =>
            p.Library != null
            && (
                p.Library.Type == type
                || (
                    type == MediaTypes.TvMediaType
                    && p.Library.LibraryTvs.Any(t => t.TvId == parsedId)
                )
                || (
                    type == MediaTypes.MovieMediaType
                    && p.Library.LibraryMovies.Any(m => m.MovieId == parsedId)
                )
            )
        );
    }

    private static PlaybackPreference CreateDefaultPlaybackPreference(
        List<IVideo> qualities,
        List<IAudio> audio,
        List<ISubtitle> captions
    )
    {
        int? width = qualities.Select(q => q.Width).FirstOrDefault();
        string? audioLanguage = audio.Select(a => a.Language).FirstOrDefault();
        string? subtitleLanguage = captions.FirstOrDefault()?.Language;
        string? subtitleType = captions.FirstOrDefault()?.Type;
        string? subtitleCodec = captions.FirstOrDefault()?.Codec;

        return new()
        {
            Video = width.HasValue ? new() { Width = width.Value } : null,
            Audio = audioLanguage is not null ? new() { Language = audioLanguage } : null,
            Subtitle = subtitleLanguage is not null
                ? new()
                {
                    Language = subtitleLanguage,
                    Type = subtitleType,
                    Codec = subtitleCodec,
                }
                : null,
        };
    }
}
