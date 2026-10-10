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

using NoMercy.Api.DTOs.Music;
using NoMercy.Database.Models.Users;

namespace NoMercy.Api.Services.Music;

public class MusicPlayerStateFactory
{
    public static MusicPlayerState Create(
        Device device,
        PlaylistTrackDto item,
        List<PlaylistTrackDto> playlist,
        string type,
        Guid listId
    )
    {
        return new()
        {
            DeviceId = device.DeviceId,
            VolumePercentage = device.VolumePercent ?? Device.DefaultVolumePercent,
            CurrentItem = item,
            Backlog = [item],
            Playlist = playlist,
            PlayState = true,
            Time = 0,
            PositionCapturedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Shuffle = false,
            Repeat = "off",
            CurrentList = new($"/music/{ToRouteSegment(type)}/{listId}", UriKind.Relative),
            Actions = new()
            {
                Disallows = new()
                {
                    // Can't pause when starting (we're playing)
                    Pausing = false,
                    // Can't resume when already playing
                    Resuming = true,
                    // Can't go to previous if backlog only has current item
                    Previous = true,
                    // Can't go to next if playlist is empty after current
                    Next = playlist.Count <= 0,
                    // Basic actions that are always allowed
                    Seeking = false,
                    Stopping = false,
                    Muting = false,
                    TogglingShuffle = false,
                    TogglingRepeatContext = false,
                    TogglingRepeatTrack = false,
                },
            },
        };
    }

    /// <summary>
    /// Maps a playback list `type` token to the REST route segment used in the
    /// emitted `CurrentList` link. All five member routes (album, artist,
    /// playlist, track, genre) are plural; the type token dispatched through
    /// StartPlaybackCommand and MusicPlaylistManager.GetPlaylist stays singular.
    /// <see cref="FromRouteSegment"/> is the inverse.
    /// </summary>
    public static string ToRouteSegment(string type) =>
        type switch
        {
            "album" => "albums",
            "artist" => "artists",
            "playlist" => "playlists",
            "track" => "tracks",
            "genre" => "genres",
            _ => type,
        };

    /// <summary>
    /// Inverse of <see cref="ToRouteSegment"/>. Recovers the semantic playback
    /// `type` token (e.g. for re-dispatching a cast handoff through
    /// StartPlaybackCommand) from a `CurrentList` route segment.
    /// </summary>
    public static string FromRouteSegment(string segment) =>
        segment switch
        {
            "albums" => "album",
            "artists" => "artist",
            "playlists" => "playlist",
            "tracks" => "track",
            "genres" => "genre",
            _ => segment,
        };
}
