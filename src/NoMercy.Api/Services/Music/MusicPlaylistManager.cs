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

using Microsoft.EntityFrameworkCore;
using NoMercy.Api.DTOs.Music;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Services.Music;

public class MusicPlaylistManager
{
    private readonly IMusicRepository _musicRepository;
    private readonly MediaContext _mediaContext;

    public MusicPlaylistManager(IMusicRepository musicService, MediaContext mediaContext)
    {
        _musicRepository = musicService;
        _mediaContext = mediaContext;
    }

    public async Task<(PlaylistTrackDto item, List<PlaylistTrackDto> playlist)> GetPlaylist(
        Guid userId,
        string type,
        Guid listId,
        Guid trackId,
        string country
    )
    {
        return type.ToLower().Trim() switch
        {
            // For type="track", the track ID is in the listId parameter (second param)
            // Call format: StartPlaybackCommand("track", trackId, null/empty)
            "track" => await GetSingleTrack(userId, listId, country),
            "playlist" => await GetPlaylistTracks(userId, listId, trackId, country),
            "album" => await GetAlbumTracks(userId, listId, trackId, country),
            "artist" => await GetArtistTracks(userId, listId, trackId, country),
            "genre" => await GetGenreTracks(userId, listId, trackId, country),
            _ => throw new ArgumentException($"Invalid playlist type: '{type}'", nameof(type)),
        };
    }

    private async Task<(PlaylistTrackDto, List<PlaylistTrackDto>)> GetSingleTrack(
        Guid userId,
        Guid trackId,
        string country
    )
    {
        Track? track = await _musicRepository.GetTrackAsync(trackId);

        if (track is null)
            throw new("Track not found");

        // Load TrackUser data for favorite status
        bool isFavorite = await _mediaContext.TrackUser.AnyAsync(tu =>
            tu.TrackId == trackId && tu.UserId == userId
        );

        if (isFavorite && !track.TrackUser.Any(tu => tu.UserId == userId))
        {
            track.TrackUser.Add(new() { TrackId = trackId, UserId = userId });
        }

        PlaylistTrackDto trackDto = new(track, country);

        // Return the track with an empty playlist (no other tracks to play)
        return (trackDto, []);
    }

    private async Task<(PlaylistTrackDto, List<PlaylistTrackDto>)> GetPlaylistTracks(
        Guid userId,
        Guid listId,
        Guid trackId,
        string country
    )
    {
        List<PlaylistTrack> playlistTracks = await _musicRepository.GetPlaylistTracksAsync(
            userId,
            listId
        );

        if (playlistTracks.Count == 0)
            throw new("Playlist track not found");

        List<PlaylistTrackDto> playlist = playlistTracks
            .Select(x => new PlaylistTrackDto(x, country))
            .ToList();

        PlaylistTrackDto item =
            playlist.FirstOrDefault(p => p.Id == trackId) ?? throw new("Playlist track not found");
        return (item, playlist.QueueAfter(track => track.Id == trackId));
    }

    private async Task<(PlaylistTrackDto, List<PlaylistTrackDto>)> GetAlbumTracks(
        Guid userId,
        Guid listId,
        Guid trackId,
        string country
    )
    {
        List<AlbumTrack> albumTracks = await _musicRepository.GetAlbumTracksAsync(userId, listId);

        if (albumTracks.Count == 0)
            throw new("Album track not found");

        List<PlaylistTrackDto> playlist = albumTracks
            .Select(x => new PlaylistTrackDto(x, country))
            .OrderBy(x => x.Disc)
            .ThenBy(x => x.Track)
            .ToList();

        PlaylistTrackDto item =
            playlist.FirstOrDefault(p => p.Id == trackId) ?? throw new("Album track not found");
        return (item, playlist.QueueAfter(track => track.Id == trackId));
    }

    private async Task<(PlaylistTrackDto, List<PlaylistTrackDto>)> GetArtistTracks(
        Guid userId,
        Guid listId,
        Guid trackId,
        string country
    )
    {
        List<ArtistTrack> artistTracks = await _musicRepository.GetArtistTracksAsync(
            userId,
            listId
        );

        if (artistTracks.Count == 0)
            throw new("Artist track not found");

        List<PlaylistTrackDto> playlist = artistTracks
            .Select(x => new PlaylistTrackDto(x, country))
            .DistinctBy(x => x.Id)
            .OrderBy(x => x.AlbumName)
            .ThenBy(x => x.Disc)
            .ThenBy(x => x.Track)
            .ToList();

        PlaylistTrackDto item =
            playlist.FirstOrDefault(p => p.Id == trackId) ?? throw new("Artist track not found");
        return (item, playlist.QueueAfter(track => track.Id == trackId));
    }

    private async Task<(PlaylistTrackDto, List<PlaylistTrackDto>)> GetGenreTracks(
        Guid userId,
        Guid listId,
        Guid trackId,
        string country
    )
    {
        List<MusicGenreTrack> genreTracks = await _musicRepository.GetGenreTracksAsync(
            userId,
            listId
        );

        if (genreTracks.Count == 0)
            throw new("Genre track not found");

        List<PlaylistTrackDto> playlist = genreTracks
            .Select(x => new PlaylistTrackDto(x, country))
            .DistinctBy(x => x.Id)
            .OrderBy(x => x.Disc)
            .ThenBy(x => x.Track)
            .ToList();

        PlaylistTrackDto item =
            playlist.FirstOrDefault(p => p.Id == trackId) ?? throw new("Genre track not found");
        return (item, playlist.QueueAfter(track => track.Id == trackId));
    }
}
