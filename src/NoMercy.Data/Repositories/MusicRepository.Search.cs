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
using NoMercy.Database;
using NoMercy.Database.Models.Music;

namespace NoMercy.Data.Repositories;

public partial class MusicRepository
{
    #region Search Operations

    public async Task<List<Guid>> SearchArtistIdsAsync(
        string normalizedQuery,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Artists.AsNoTracking()
            .Where(artist => MediaContext.NormalizeSearch(artist.Name).Contains(normalizedQuery))
            .OrderBy(artist => artist.Name)
            .ThenBy(artist => artist.Id)
            .Select(artist => artist.Id)
            .ToListAsync(ct);
    }

    public async Task<List<Guid>> SearchAlbumIdsAsync(
        string normalizedQuery,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Albums.AsNoTracking()
            .Where(album => MediaContext.NormalizeSearch(album.Name).Contains(normalizedQuery))
            .OrderBy(album => album.Name)
            .ThenBy(album => album.Id)
            .Select(album => album.Id)
            .ToListAsync(ct);
    }

    public async Task<List<Guid>> SearchPlaylistIdsAsync(
        string normalizedQuery,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Playlists.AsNoTracking()
            .Where(playlist =>
                MediaContext.NormalizeSearch(playlist.Name).Contains(normalizedQuery)
            )
            .OrderBy(playlist => playlist.Name)
            .ThenBy(playlist => playlist.Id)
            .Select(playlist => playlist.Id)
            .ToListAsync(ct);
    }

    public async Task<List<Guid>> SearchTrackIdsAsync(
        string normalizedQuery,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Tracks.AsNoTracking()
            .Where(track => MediaContext.NormalizeSearch(track.Name).Contains(normalizedQuery))
            .OrderBy(track => track.Name)
            .ThenBy(track => track.Id)
            .Select(track => track.Id)
            .ToListAsync(ct);
    }

    public async Task<List<Artist>> GetArtistsByIdsAsync(
        List<Guid> artistIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Artists.AsNoTracking()
            .Where(artist => artistIds.Contains(artist.Id))
            .OrderBy(artist => artist.Name)
            .ThenBy(artist => artist.Id)
            .Include(artist => artist.ArtistTrack)
                .ThenInclude(artistTrack => artistTrack.Track)
            .Include(artist => artist.AlbumArtist)
                .ThenInclude(albumArtist => albumArtist.Album)
            .ToListAsync(ct);
    }

    public async Task<List<Album>> GetAlbumsByIdsAsync(
        List<Guid> albumIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Albums.AsNoTracking()
            .Where(album => albumIds.Contains(album.Id))
            .OrderBy(album => album.Name)
            .ThenBy(album => album.Id)
            .Include(album => album.AlbumTrack)
                .ThenInclude(albumTrack => albumTrack.Track)
                    .ThenInclude(track => track.ArtistTrack)
                        .ThenInclude(artistTrack => artistTrack.Artist)
            .Include(album => album.AlbumTrack)
                .ThenInclude(albumTrack => albumTrack.Track)
                    .ThenInclude(track => track.TrackUser)
            .ToListAsync(ct);
    }

    public async Task<List<Playlist>> GetUserPlaylistsAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Playlists.AsNoTracking()
            .Where(playlist => playlist.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<List<Playlist>> GetPlaylistsByIdsAsync(
        List<Guid> playlistIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Playlists.AsNoTracking()
            .Where(playlist => playlistIds.Contains(playlist.Id))
            .OrderBy(playlist => playlist.Name)
            .ThenBy(playlist => playlist.Id)
            .Include(playlist => playlist.Tracks)
                .ThenInclude(playlistTrack => playlistTrack.Track)
                    .ThenInclude(track => track.TrackUser)
            .ToListAsync(ct);
    }

    public async Task<List<Track>> GetTracksByIdsAsync(
        List<Guid> trackIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Tracks.AsNoTracking()
            .Where(track => trackIds.Contains(track.Id))
            .OrderBy(track => track.Name)
            .ThenBy(track => track.Id)
            .Include(track => track.ArtistTrack)
                .ThenInclude(artistTrack => artistTrack.Artist)
            .Include(track => track.AlbumTrack)
                .ThenInclude(albumTrack => albumTrack.Album)
            .Include(track => track.PlaylistTrack)
                .ThenInclude(playlistTrack => playlistTrack.Playlist)
            .Include(track => track.TrackUser)
            .ToListAsync(ct);
    }

    #endregion

    #region Projection Methods — Search Cross-Reference

    public async Task<List<Guid>> GetArtistIdsFromAlbumsAsync(
        List<Guid> albumIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .AlbumTrack.AsNoTracking()
            .Where(at => albumIds.Contains(at.AlbumId))
            .SelectMany(at => at.Track.ArtistTrack)
            .Select(at => at.ArtistId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<List<Guid>> GetArtistIdsFromPlaylistTracksAsync(
        List<Guid> playlistIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .PlaylistTrack.AsNoTracking()
            .Where(pt => playlistIds.Contains(pt.PlaylistId))
            .SelectMany(pt => pt.Track.ArtistTrack)
            .Select(at => at.ArtistId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<List<Guid>> GetArtistIdsFromTracksAsync(
        List<Guid> trackIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .ArtistTrack.AsNoTracking()
            .Where(at => trackIds.Contains(at.TrackId))
            .Select(at => at.ArtistId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<List<Guid>> GetAlbumIdsFromTracksAsync(
        List<Guid> trackIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .AlbumTrack.AsNoTracking()
            .Where(at => trackIds.Contains(at.TrackId))
            .Select(at => at.AlbumId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<List<SearchTrackCardDto>> SearchTrackCardsAsync(
        List<Guid> trackIds,
        Guid userId,
        string country,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Tracks.AsNoTracking()
            .Where(track => trackIds.Contains(track.Id))
            .Select(track => new SearchTrackCardDto
            {
                Id = track.Id,
                Name = track.Name,
                FolderId = track.FolderId,
                Folder = track.Folder,
                Filename = track.Filename,
                Cover = track.Cover,
                ColorPalette = track._colorPalette ?? string.Empty,
                Duration = track.Duration,
                DiscNumber = track.DiscNumber,
                TrackNumber = track.TrackNumber,
                Quality = track.Quality,
                UpdatedAt = track.UpdatedAt,
                Favorite = track.TrackUser.Any(tu => tu.UserId == userId),
                AlbumId = track.AlbumTrack.Select(at => at.AlbumId.ToString()).FirstOrDefault(),
                AlbumName = track.AlbumTrack.Select(at => at.Album.Name).FirstOrDefault(),
                AlbumCover = track.AlbumTrack.Select(at => at.Album.Cover).FirstOrDefault(),
                AlbumColorPalette = track
                    .AlbumTrack.Select(at => at.Album._colorPalette)
                    .FirstOrDefault(),
                ArtistCover = track.ArtistTrack.Select(at => at.Artist.Cover).FirstOrDefault(),
                ArtistColorPalette = track
                    .ArtistTrack.Select(at => at.Artist._colorPalette)
                    .FirstOrDefault(),
                Artists = track
                    .ArtistTrack.Select(at => new SearchTrackArtistDto
                    {
                        Id = at.ArtistId,
                        Name = at.Artist.Name,
                    })
                    .ToList(),
                Albums = track
                    .AlbumTrack.Select(at => new SearchTrackAlbumDto
                    {
                        Id = at.AlbumId,
                        Name = at.Album.Name,
                    })
                    .ToList(),
            })
            .ToListAsync(ct);
    }

    #endregion

    #region Search — Parallel Full-Data Fetch

    public async Task<MusicSearchFullData> SearchMusicFullDataAsync(
        List<Guid> artistIds,
        List<Guid> albumIds,
        List<Guid> playlistIds,
        List<Guid> trackIds,
        CancellationToken ct = default
    )
    {
        // Each entity set is fetched via its own repository method (which manages its own DbContext)
        // so the four queries run in parallel without sharing a (non-thread-safe) context.
        Task<List<Artist>> artistsTask = Task.Run(
            async () => await GetArtistsByIdsAsync(artistIds, ct),
            ct
        );

        Task<List<Album>> albumsTask = Task.Run(
            async () => await GetAlbumsByIdsAsync(albumIds, ct),
            ct
        );

        Task<List<Playlist>> playlistsTask = Task.Run(
            async () => await GetPlaylistsByIdsAsync(playlistIds, ct),
            ct
        );

        Task<List<Track>> tracksTask = Task.Run(
            async () => await GetTracksByIdsAsync(trackIds, ct),
            ct
        );

        await Task.WhenAll(artistsTask, albumsTask, playlistsTask, tracksTask);

        return new(artistsTask.Result, albumsTask.Result, playlistsTask.Result, tracksTask.Result);
    }

    #endregion
}
