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
using NoMercy.Data.Extensions;
using NoMercy.Database;
using NoMercy.Database.Models.Music;

namespace NoMercy.Data.Repositories;

public partial class MusicRepository
{
    #region Album Queries

    public async Task<Album?> GetAlbumAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        // Minimal include set — drops per-track TrackUser join and the
        // track -> artist -> translations fan-out which caused the same
        // timeout pattern as GetArtistAsync for large albums.
        return await mediaContext
            .Albums.AsNoTracking()
            .AsSplitQuery()
            .Where(album => album.Id == id)
            .ForUser(userId)
            .Include(album => album.Library)
            .Include(album => album.AlbumUser.Where(au => au.UserId == userId))
            .Include(album =>
                album.AlbumTrack.AsQueryable().Where(TrackAvailability.AlbumTrackIsPlayable)
            )
                .ThenInclude(albumTrack => albumTrack.Track)
                    .ThenInclude(track => track.ArtistTrack)
                        .ThenInclude(artistTrack => artistTrack.Artist)
            .Include(album => album.AlbumArtist)
                .ThenInclude(albumArtist => albumArtist.Artist)
            .Include(album => album.Images)
            .Include(album => album.Translations)
            .Include(album => album.AlbumMusicGenre)
                .ThenInclude(amg => amg.MusicGenre)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<Album>> GetAlbums(
        Guid userId,
        string letter,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Albums.AsNoTracking()
            .ForUser(userId)
            .Where(album =>
                (letter == "_" || letter == "#")
                    ? !AlphaLetters.Any(p =>
                        (album.TitleSort ?? album.Name).ToLower().StartsWith(p)
                    )
                    : (album.TitleSort ?? album.Name).ToLower().StartsWith(letter.ToLower())
            )
            .Include(album => album.AlbumUser.Where(au => au.UserId == userId))
            .Include(album => album.Translations)
            .Include(album => album.Images.Where(image => image.Type == "background"))
            .Include(album => album.AlbumMusicGenre)
                .ThenInclude(amg => amg.MusicGenre)
            .ToListAsync(ct);
    }

    public async Task LikeAlbumAsync(
        Guid userId,
        Album album,
        bool liked,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        if (liked)
        {
            await mediaContext
                .AlbumUser.Upsert(new(album.Id, userId))
                .On(m => new { m.AlbumId, m.UserId })
                .WhenMatched(m => new() { AlbumId = m.AlbumId, UserId = m.UserId })
                .RunAsync();
        }
        else
        {
            AlbumUser? albumUser = await mediaContext.AlbumUser.FirstOrDefaultAsync(
                au => au.AlbumId == album.Id && au.UserId == userId,
                ct
            );

            if (albumUser is not null)
            {
                mediaContext.AlbumUser.Remove(albumUser);
                await mediaContext.SaveChangesAsync(ct);
            }
        }
    }

    public async Task<List<AlbumTrack>> GetAlbumTracksForIdsAsync(
        List<Guid> albumIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .AlbumTrack.AsNoTracking()
            .Playable()
            .Where(at => albumIds.Contains(at.AlbumId))
            .Include(at => at.Track)
            .ToListAsync(ct);
    }

    #endregion

    #region Projection Methods — Album Cards

    public async Task<List<AlbumCardDto>> GetAlbumCardsAsync(
        Guid userId,
        string letter,
        string language,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Albums.AsNoTracking()
            .ForUser(userId)
            .Where(album =>
                (letter == "_" || letter == "#")
                    ? !AlphaLetters.Any(p =>
                        (album.TitleSort ?? album.Name).ToLower().StartsWith(p)
                    )
                    : (album.TitleSort ?? album.Name).ToLower().StartsWith(letter.ToLower())
            )
            .Where(album =>
                album
                    .AlbumTrack.AsQueryable()
                    .Where(TrackAvailability.AlbumTrackIsPlayable)
                    .Any(at => at.Track.Duration != null)
            )
            .OrderBy(album => album.TitleSort ?? album.Name)
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
                TrackCount = album
                    .AlbumTrack.AsQueryable()
                    .Where(TrackAvailability.AlbumTrackIsPlayable)
                    .Count(at => at.Track.Duration != null),
                ArtistName = album
                    .AlbumArtist.OrderBy(link => link.Artist.Name)
                    .Select(link => link.Artist.Name)
                    .FirstOrDefault(),
                TranslatedDescription = album
                    .Translations.Where(t => t.Iso31661 == language)
                    .Select(t => t.Description)
                    .FirstOrDefault(),
                BackgroundImagePath = album
                    .Images.Where(image => image.Type == "background")
                    .Select(image => image.FilePath)
                    .FirstOrDefault(),
                BackgroundImageColorPalette = album
                    .Images.Where(image => image.Type == "background")
                    .Select(image => image._colorPalette)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns every album in the library as cards, ordered by name. Used by
    /// the TV lolomo layout to build one carousel per first-letter bucket.
    /// </summary>
    public async Task<List<AlbumCardDto>> GetAllAlbumCardsAsync(
        Guid userId,
        string language,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Albums.AsNoTracking()
            .ForUser(userId)
            .Where(album =>
                album
                    .AlbumTrack.AsQueryable()
                    .Where(TrackAvailability.AlbumTrackIsPlayable)
                    .Any(at => at.Track.Duration != null)
            )
            .OrderBy(album => album.TitleSort ?? album.Name)
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
                TrackCount = album
                    .AlbumTrack.AsQueryable()
                    .Where(TrackAvailability.AlbumTrackIsPlayable)
                    .Count(at => at.Track.Duration != null),
                ArtistName = album
                    .AlbumArtist.OrderBy(link => link.Artist.Name)
                    .Select(link => link.Artist.Name)
                    .FirstOrDefault(),
                TranslatedDescription = album
                    .Translations.Where(t => t.Iso31661 == language)
                    .Select(t => t.Description)
                    .FirstOrDefault(),
                BackgroundImagePath = album
                    .Images.Where(image => image.Type == "background")
                    .Select(image => image.FilePath)
                    .FirstOrDefault(),
                BackgroundImageColorPalette = album
                    .Images.Where(image => image.Type == "background")
                    .Select(image => image._colorPalette)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
    }

    public async Task<List<AlbumCardDto>> GetLatestAlbumCardsAsync(
        int take = 36,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Albums.AsNoTracking()
            .Where(album =>
                !string.IsNullOrEmpty(album.Cover)
                && album.AlbumTrack.AsQueryable().Any(TrackAvailability.AlbumTrackIsPlayable)
            )
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
                TrackCount = album
                    .AlbumTrack.AsQueryable()
                    .Count(TrackAvailability.AlbumTrackIsPlayable),
                ArtistName = album
                    .AlbumArtist.OrderBy(link => link.Artist.Name)
                    .Select(link => link.Artist.Name)
                    .FirstOrDefault(),
            })
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<List<AlbumCardDto>> GetFavoriteAlbumCardsAsync(
        Guid userId,
        int take = 36,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
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
                TrackCount = albumUser
                    .Album.AlbumTrack.AsQueryable()
                    .Count(TrackAvailability.AlbumTrackIsPlayable),
                ArtistName = albumUser
                    .Album.AlbumArtist.OrderBy(link => link.Artist.Name)
                    .Select(link => link.Artist.Name)
                    .FirstOrDefault(),
            })
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<List<AlbumCardDto>> GetAlbumCardsByIdsAsync(
        List<Guid> albumIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Albums.AsNoTracking()
            .Where(album => albumIds.Contains(album.Id))
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
                TrackCount = album
                    .AlbumTrack.AsQueryable()
                    .Count(TrackAvailability.AlbumTrackIsPlayable),
                ArtistName = album
                    .AlbumArtist.OrderBy(link => link.Artist.Name)
                    .Select(link => link.Artist.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
    }

    #endregion
}
