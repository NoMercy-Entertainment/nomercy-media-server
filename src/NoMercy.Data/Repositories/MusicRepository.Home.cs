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
    #region Home Page Methods

    public async Task<List<Album>> GetLatestAlbums(CancellationToken ct = default)
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Albums.AsNoTracking()
            .Where(album => !string.IsNullOrEmpty(album.Cover) && album.AlbumTrack.Any())
            .Include(album => album.AlbumTrack)
                .ThenInclude(albumTrack => albumTrack.Track)
            .OrderByDescending(album => album.CreatedAt)
            .ThenBy(album => album.Id)
            .ToListAsync(ct);
    }

    public async Task<List<Artist>> GetLatestArtists(CancellationToken ct = default)
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Artists.AsNoTracking()
            .Where(artist => !string.IsNullOrEmpty(artist.Cover) && artist.ArtistTrack.Any())
            .Include(artist => artist.Images.Where(image => image.Type == "thumb"))
            .Include(artist => artist.ArtistTrack)
                .ThenInclude(artistTrack => artistTrack.Track)
            .OrderByDescending(artist => artist.CreatedAt)
            .ThenBy(artist => artist.Id)
            .ToListAsync(ct);
    }

    public async Task<List<MusicGenre>> GetLatestGenres(CancellationToken ct = default)
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .MusicGenres.AsNoTracking()
            .Where(genre => genre.MusicGenreTracks.Any())
            .Include(genre => genre.MusicGenreTracks)
            .OrderByDescending(genre => genre.MusicGenreTracks.Count)
            .ThenBy(genre => genre.Id)
            .ToListAsync(ct);
    }

    public async Task<List<ArtistTrack>> GetFavoriteArtistAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .MusicPlays.AsNoTracking()
            .Where(musicPlay => musicPlay.UserId == userId)
            .Include(musicPlay => musicPlay.Track)
                .ThenInclude(track => track.ArtistTrack)
                    .ThenInclude(artistTrack => artistTrack.Artist)
                        .ThenInclude(artist => artist.Images.Where(image => image.Type == "thumb"))
            .SelectMany(p => p.Track.ArtistTrack)
            .ToListAsync(ct);
    }

    public async Task<List<AlbumTrack>> GetFavoriteAlbumAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .MusicPlays.AsNoTracking()
            .Where(musicPlay => musicPlay.UserId == userId)
            .Include(musicPlay => musicPlay.Track)
                .ThenInclude(track => track.AlbumTrack)
                    .ThenInclude(albumTrack => albumTrack.Album)
            .SelectMany(p => p.Track.AlbumTrack)
            .ToListAsync(ct);
    }

    public async Task<List<PlaylistTrack>> GetFavoritePlaylistAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .MusicPlays.AsNoTracking()
            .Where(musicPlay =>
                musicPlay.Track.PlaylistTrack.All(pt => pt.Playlist.UserId == userId)
            )
            .Include(musicPlay => musicPlay.Track)
                .ThenInclude(track => track.PlaylistTrack)
                    .ThenInclude(playlistTrack => playlistTrack.Playlist)
            .SelectMany(p => p.Track.PlaylistTrack)
            .ToListAsync(ct);
    }

    public async Task<List<ArtistUser>> GetFavoriteArtists(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .ArtistUser.AsNoTracking()
            .Where(artistUser => artistUser.UserId == userId)
            .Include(artistUser => artistUser.Artist)
                .ThenInclude(artist => artist.ArtistTrack)
                    .ThenInclude(artistTrack => artistTrack.Track)
            .Include(artistUser => artistUser.Artist)
                .ThenInclude(artist => artist.Images.Where(image => image.Type == "thumb"))
            .ToListAsync(ct);
    }

    public async Task<List<AlbumUser>> GetFavoriteAlbums(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .AlbumUser.AsNoTracking()
            .Where(albumUser => albumUser.UserId == userId)
            .Include(albumUser => albumUser.Album)
                .ThenInclude(album => album.AlbumTrack)
                    .ThenInclude(albumTrack => albumTrack.Track)
            .ToListAsync(ct);
    }

    #endregion

    #region Collection Operations (for CollectionsController)

    public async Task<List<TrackUser>> GetFavoriteTracks(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .TrackUser.AsNoTracking()
            .Where(trackUser => trackUser.UserId == userId)
            .Include(trackUser => trackUser.Track)
                .ThenInclude(track => track.ArtistTrack)
                    .ThenInclude(artistTrack => artistTrack.Artist)
            .Include(trackUser => trackUser.Track)
                .ThenInclude(track => track.AlbumTrack)
                    .ThenInclude(albumTrack => albumTrack.Album)
            .ToListAsync(ct);
    }

    public async Task<List<ArtistTrack>> GetArtistTracksForCollectionAsync(
        List<Guid> artistIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .ArtistTrack.AsNoTracking()
            .Where(artistTrack => artistIds.Contains(artistTrack.ArtistId))
            .Include(artistTrack => artistTrack.Track)
            .ToListAsync(ct);
    }

    #endregion

    #region Projection Methods — Genre Cards

    public async Task<List<MusicGenreCardDto>> GetLatestGenreCardsAsync(
        int take = 36,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .MusicGenres.AsNoTracking()
            .Where(genre => genre.MusicGenreTracks.Any())
            .OrderByDescending(genre => genre.MusicGenreTracks.Count())
            .ThenBy(genre => genre.Id)
            .Select(genre => new MusicGenreCardDto
            {
                Id = genre.Id,
                Name = genre.Name,
                TrackCount = genre.MusicGenreTracks.Count(),
            })
            .Take(take)
            .ToListAsync(ct);
    }

    #endregion

    #region Projection Methods — Top Music (Favorites)

    // These run the same queries the start page builds in parallel below. They
    // were separate copies until one of them stopped projecting Link, which the
    // card then dereferenced — the favorites row 500'd for anyone who had ever
    // played a track.
    public async Task<TopMusicItemDto?> GetTopArtistAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await GetTopArtistQuery(mediaContext, userId).FirstOrDefaultAsync(ct);
    }

    public async Task<TopMusicItemDto?> GetTopAlbumAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await GetTopAlbumQuery(mediaContext, userId).FirstOrDefaultAsync(ct);
    }

    public async Task<TopMusicItemDto?> GetTopPlaylistAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await GetTopPlaylistQuery(mediaContext, userId).FirstOrDefaultAsync(ct);
    }

    #endregion

    #region Parallel Music Start Page

    public async Task<MusicStartPageData> GetMusicStartPageAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        // Run 3 groups in parallel — each group gets its own DbContext
        Task<(TopMusicItemDto?, TopMusicItemDto?, TopMusicItemDto?)> topTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                TopMusicItemDto? artist = await GetTopArtistQuery(ctx, userId)
                    .FirstOrDefaultAsync(ct);
                TopMusicItemDto? album = await GetTopAlbumQuery(ctx, userId)
                    .FirstOrDefaultAsync(ct);
                TopMusicItemDto? playlist = await GetTopPlaylistQuery(ctx, userId)
                    .FirstOrDefaultAsync(ct);
                return (artist, album, playlist);
            },
            ct
        );

        Task<(List<ArtistCardDto>, List<AlbumCardDto>, List<PlaylistCardDto>)> favoritesTask =
            Task.Run(
                async () =>
                {
                    await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                    List<ArtistCardDto> artists = await GetFavoriteArtistCardsQuery(ctx, userId)
                        .Take(36)
                        .ToListAsync(ct);
                    List<AlbumCardDto> albums = await GetFavoriteAlbumCardsQuery(ctx, userId)
                        .Take(36)
                        .ToListAsync(ct);
                    List<PlaylistCardDto> playlists = await GetPlaylistCardsQuery(ctx, userId)
                        .Take(36)
                        .ToListAsync(ct);
                    await MusicGroupCovers.FillPlaylistsAsync(ctx, playlists, ct);
                    return (artists, albums, playlists);
                },
                ct
            );

        Task<(List<ArtistCardDto>, List<MusicGenreCardDto>, List<AlbumCardDto>)> latestTask =
            Task.Run(
                async () =>
                {
                    await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                    List<ArtistCardDto> artists = await GetLatestArtistCardsQuery(ctx)
                        .Take(36)
                        .ToListAsync(ct);
                    List<MusicGenreCardDto> genres = await GetLatestGenreCardsQuery(ctx)
                        .Take(36)
                        .ToListAsync(ct);
                    await MusicGroupCovers.FillGenresAsync(ctx, userId, genres, ct);
                    List<AlbumCardDto> albums = await GetLatestAlbumCardsQuery(ctx)
                        .Take(36)
                        .ToListAsync(ct);
                    return (artists, genres, albums);
                },
                ct
            );

        await Task.WhenAll(topTask, favoritesTask, latestTask);

        (TopMusicItemDto? topArtist, TopMusicItemDto? topAlbum, TopMusicItemDto? topPlaylist) =
            topTask.Result;
        (
            List<ArtistCardDto> favArtists,
            List<AlbumCardDto> favAlbums,
            List<PlaylistCardDto> playlists
        ) = favoritesTask.Result;
        (
            List<ArtistCardDto> latestArtists,
            List<MusicGenreCardDto> latestGenres,
            List<AlbumCardDto> latestAlbums
        ) = latestTask.Result;

        return new()
        {
            TopArtist = topArtist,
            TopAlbum = topAlbum,
            TopPlaylist = topPlaylist,
            FavoriteArtists = favArtists,
            FavoriteAlbums = favAlbums,
            Playlists = playlists,
            LatestArtists = latestArtists,
            LatestGenres = latestGenres,
            LatestAlbums = latestAlbums,
        };
    }

    // Static query builders for parallel execution with arbitrary MediaContext instances

    private static IQueryable<TopMusicItemDto> GetTopArtistQuery(MediaContext ctx, Guid userId)
    {
        return ctx
            .MusicPlays.AsNoTracking()
            .Where(mp => mp.UserId == userId)
            .SelectMany(mp => mp.Track.ArtistTrack)
            .GroupBy(at => new
            {
                at.Artist.Id,
                at.Artist.Name,
                at.Artist.Cover,
                ColorPalette = at.Artist._colorPalette ?? string.Empty,
            })
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Id)
            .Select(g => new TopMusicItemDto
            {
                Id = g.Key.Id.ToString(),
                Name = g.Key.Name,
                Cover = g.Key.Cover,
                ColorPalette = g.Key.ColorPalette,
                Type = "artist",
                Link = new($"/music/artists/{g.Key.Id}", UriKind.Relative),
            });
    }

    private static IQueryable<TopMusicItemDto> GetTopAlbumQuery(MediaContext ctx, Guid userId)
    {
        return ctx
            .MusicPlays.AsNoTracking()
            .Where(mp => mp.UserId == userId)
            .SelectMany(mp => mp.Track.AlbumTrack)
            .GroupBy(at => new
            {
                at.Album.Id,
                at.Album.Name,
                at.Album.Cover,
                ColorPalette = at.Album._colorPalette ?? string.Empty,
            })
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Id)
            .Select(g => new TopMusicItemDto
            {
                Id = g.Key.Id.ToString(),
                Name = g.Key.Name,
                Cover = g.Key.Cover,
                ColorPalette = g.Key.ColorPalette,
                Type = "album",
                Link = new($"/music/albums/{g.Key.Id}", UriKind.Relative),
            });
    }

    private static IQueryable<TopMusicItemDto> GetTopPlaylistQuery(MediaContext ctx, Guid userId)
    {
        return ctx
            .MusicPlays.AsNoTracking()
            .Where(mp => mp.Track.PlaylistTrack.Any(pt => pt.Playlist.UserId == userId))
            .SelectMany(mp => mp.Track.PlaylistTrack)
            .Where(pt => pt.Playlist.UserId == userId)
            .GroupBy(pt => new
            {
                pt.Playlist.Id,
                pt.Playlist.Name,
                pt.Playlist.Cover,
                ColorPalette = pt.Playlist._colorPalette ?? string.Empty,
            })
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Id)
            .Select(g => new TopMusicItemDto
            {
                Id = g.Key.Id.ToString(),
                Name = g.Key.Name,
                Cover = g.Key.Cover,
                ColorPalette = g.Key.ColorPalette,
                Type = "playlist",
                Link = new($"/music/playlists/{g.Key.Id}", UriKind.Relative),
            });
    }

    private static IQueryable<ArtistCardDto> GetFavoriteArtistCardsQuery(
        MediaContext ctx,
        Guid userId
    )
    {
        return ctx
            .ArtistUser.AsNoTracking()
            .Where(artistUser => artistUser.UserId == userId)
            .OrderBy(artistUser => artistUser.Artist.Name)
            .ThenBy(artistUser => artistUser.Artist.Id)
            .Select(artistUser => new ArtistCardDto
            {
                Id = artistUser.Artist.Id,
                Name = artistUser.Artist.Name,
                Cover = artistUser.Artist.Cover,
                Disambiguation = artistUser.Artist.Disambiguation,
                Description = artistUser.Artist.Description,
                ColorPalette = artistUser.Artist._colorPalette ?? string.Empty,
                LibraryId = artistUser.Artist.LibraryId,
                Folder = artistUser.Artist.Folder,
                TrackCount = artistUser.Artist.ArtistTrack.Count(),
                Link = new($"/music/artists/{artistUser.Artist.Id}", UriKind.Relative),
                ThumbImagePath = artistUser
                    .Artist.Images.Where(image => image.Type == "thumb")
                    .Select(image => image.FilePath)
                    .FirstOrDefault(),
            });
    }

    private static IQueryable<AlbumCardDto> GetFavoriteAlbumCardsQuery(
        MediaContext ctx,
        Guid userId
    )
    {
        return ctx
            .AlbumUser.AsNoTracking()
            .Where(albumUser => albumUser.UserId == userId)
            .OrderBy(albumUser => albumUser.Album.Name)
            .ThenBy(albumUser => albumUser.Album.Id)
            .Select(albumUser => new AlbumCardDto
            {
                Id = albumUser.Album.Id,
                Name = albumUser.Album.Name,
                Cover = albumUser.Album.Cover,
                Disambiguation = albumUser.Album.Disambiguation,
                Description = albumUser.Album.Description,
                ColorPalette = albumUser.Album._colorPalette ?? string.Empty,
                LibraryId = albumUser.Album.LibraryId,
                Folder = albumUser.Album.Folder,
                Year = albumUser.Album.Year,
                TrackCount = albumUser.Album.AlbumTrack.Count(),
                ArtistName = albumUser
                    .Album.AlbumArtist.OrderBy(link => link.Artist.Name)
                    .Select(link => link.Artist.Name)
                    .FirstOrDefault(),
                Link = new($"/music/albums/{albumUser.Album.Id}", UriKind.Relative),
            });
    }

    private static IQueryable<PlaylistCardDto> GetPlaylistCardsQuery(MediaContext ctx, Guid userId)
    {
        return ctx
            .Playlists.AsNoTracking()
            .Where(playlist => playlist.UserId == userId)
            .OrderBy(playlist => playlist.Name)
            .ThenBy(playlist => playlist.Id)
            .Select(playlist => new PlaylistCardDto
            {
                Id = playlist.Id,
                Name = playlist.Name,
                Cover = playlist.Cover,
                Description = playlist.Description,
                ColorPalette = playlist._colorPalette ?? string.Empty,
                TrackCount = playlist.Tracks.Count(),
                Link = new($"/music/playlists/{playlist.Id}", UriKind.Relative),
            });
    }

    private static IQueryable<ArtistCardDto> GetLatestArtistCardsQuery(MediaContext ctx)
    {
        return ctx
            .Artists.AsNoTracking()
            .Where(artist => !string.IsNullOrEmpty(artist.Cover) && artist.ArtistTrack.Any())
            .OrderByDescending(artist => artist.CreatedAt)
            .ThenBy(artist => artist.Id)
            .Select(artist => new ArtistCardDto
            {
                Id = artist.Id,
                Name = artist.Name,
                Cover = artist.Cover,
                Disambiguation = artist.Disambiguation,
                Description = artist.Description,
                ColorPalette = artist._colorPalette ?? string.Empty,
                LibraryId = artist.LibraryId,
                Folder = artist.Folder,
                TrackCount = artist.ArtistTrack.Count(),
                Link = new($"/music/artists/{artist.Id}", UriKind.Relative),
                ThumbImagePath = artist
                    .Images.Where(image => image.Type == "thumb")
                    .Select(image => image.FilePath)
                    .FirstOrDefault(),
            });
    }

    private static IQueryable<MusicGenreCardDto> GetLatestGenreCardsQuery(MediaContext ctx)
    {
        return ctx
            .MusicGenres.AsNoTracking()
            .Where(genre => genre.MusicGenreTracks.Any())
            .OrderByDescending(genre => genre.MusicGenreTracks.Count())
            .ThenBy(genre => genre.Id)
            .Select(genre => new MusicGenreCardDto
            {
                Id = genre.Id,
                Name = genre.Name,
                TrackCount = genre.MusicGenreTracks.Count(),
                Link = new($"/music/genres/{genre.Id}", UriKind.Relative),
            });
    }

    private static IQueryable<AlbumCardDto> GetLatestAlbumCardsQuery(MediaContext ctx)
    {
        return ctx
            .Albums.AsNoTracking()
            .Where(album => !string.IsNullOrEmpty(album.Cover) && album.AlbumTrack.Any())
            .OrderByDescending(album => album.CreatedAt)
            .ThenBy(album => album.Id)
            .Select(album => new AlbumCardDto
            {
                Id = album.Id,
                Name = album.Name,
                Cover = album.Cover,
                Disambiguation = album.Disambiguation,
                Description = album.Description,
                ColorPalette = album._colorPalette ?? string.Empty,
                LibraryId = album.LibraryId,
                Folder = album.Folder,
                Year = album.Year,
                TrackCount = album.AlbumTrack.Count(),
                ArtistName = album
                    .AlbumArtist.OrderBy(link => link.Artist.Name)
                    .Select(link => link.Artist.Name)
                    .FirstOrDefault(),
                Link = new($"/music/albums/{album.Id}", UriKind.Relative),
            });
    }

    #endregion
}
