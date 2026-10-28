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
    #region Artist Queries

    public async Task<Artist?> GetArtistAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        // Explicit include set covering every navigation the DTO touches.
        // Any missing leaf triggers lazy-load per-track and times out artists
        // like Ed Sheeran. `MusicPlays` is deliberately excluded; DTO's
        // `favorite_tracks` is returned empty and a dedicated endpoint will
        // replace it later.
        Artist? artist = await mediaContext
            .Artists.AsNoTracking()
            .AsSplitQuery()
            .Where(artist => artist.Id == id)
            .ForUser(userId)
            .Include(artist => artist.Library)
            .Include(artist => artist.ArtistUser.Where(au => au.UserId == userId))
            .Include(artist => artist.Translations)
            .Include(artist => artist.Images)
            .Include(artist => artist.AlbumArtist)
                .ThenInclude(albumArtist => albumArtist.Album)
                    .ThenInclude(album => album.AlbumUser.Where(au => au.UserId == userId))
            .Include(artist => artist.AlbumArtist)
                .ThenInclude(albumArtist => albumArtist.Album)
                    .ThenInclude(album => album.Images)
            .Include(artist =>
                artist.ArtistTrack.AsQueryable().Where(TrackAvailability.ArtistTrackIsPlayable)
            )
                .ThenInclude(at => at.Track)
                    .ThenInclude(track => track.AlbumTrack)
                        .ThenInclude(albumTrack => albumTrack.Album)
            .Include(artist =>
                artist.ArtistTrack.AsQueryable().Where(TrackAvailability.ArtistTrackIsPlayable)
            )
                .ThenInclude(at => at.Track)
                    .ThenInclude(track => track.TrackUser.Where(tu => tu.UserId == userId))
            .Include(artist => artist.ArtistMusicGenre)
                .ThenInclude(amg => amg.MusicGenre)
            .FirstOrDefaultAsync(ct);

        if (artist is null)
            return null;

        // Hydrate Track.ArtistTrack with collaborator artists. The cycle
        // Artist -> ArtistTrack -> Track -> ArtistTrack -> Artist can't be
        // expressed in a single Include under AsNoTracking, so we fetch the
        // collaborator rows in a separate flat query and reattach them.
        List<Guid> trackIds = artist.ArtistTrack.Select(at => at.TrackId).Distinct().ToList();

        if (trackIds.Count <= 0)
            return artist;

        List<ArtistTrack> collabs = await mediaContext
            .ArtistTrack.AsNoTracking()
            .Where(at => trackIds.Contains(at.TrackId))
            .Include(at => at.Artist)
            .ToListAsync(ct);

        Dictionary<Guid, List<ArtistTrack>> byTrackId = collabs
            .GroupBy(at => at.TrackId)
            .ToDictionary(g => g.Key, g => g.DistinctBy(at => at.ArtistId).ToList());

        foreach (ArtistTrack at in artist.ArtistTrack)
        {
            if (byTrackId.TryGetValue(at.TrackId, out List<ArtistTrack>? list))
            {
                at.Track.ArtistTrack = list;
            }
        }

        return artist;
    }

    public async Task<List<Artist>> GetArtists(
        Guid userId,
        string letter,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Artists.AsNoTracking()
            .ForUser(userId)
            .Where(artist =>
                (letter == "_" || letter == "#")
                    ? !AlphaLetters.Any(p =>
                        (artist.TitleSort ?? artist.Name).ToLower().StartsWith(p)
                    )
                    : (artist.TitleSort ?? artist.Name).ToLower().StartsWith(letter.ToLower())
            )
            .Include(artist => artist.ArtistUser.Where(au => au.UserId == userId))
            .Include(artist => artist.Translations)
            .Include(artist => artist.Images.Where(image => image.Type == "background"))
            .Include(artist => artist.ArtistMusicGenre)
                .ThenInclude(amg => amg.MusicGenre)
            .ToListAsync(ct);
    }

    public async Task LikeArtistAsync(
        Guid userId,
        Artist artist,
        bool liked,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        if (liked)
        {
            await mediaContext
                .ArtistUser.Upsert(new(artist.Id, userId))
                .On(m => new { m.ArtistId, m.UserId })
                .WhenMatched(m => new() { ArtistId = m.ArtistId, UserId = m.UserId })
                .RunAsync();
        }
        else
        {
            ArtistUser? artistUser = await mediaContext.ArtistUser.FirstOrDefaultAsync(
                au => au.ArtistId == artist.Id && au.UserId == userId,
                ct
            );

            if (artistUser is not null)
            {
                mediaContext.ArtistUser.Remove(artistUser);
                await mediaContext.SaveChangesAsync(ct);
            }
        }
    }

    #endregion

    #region Projection Methods — Artist Cards

    public async Task<List<ArtistCardDto>> GetArtistCardsAsync(
        Guid userId,
        string letter,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        List<ArtistCardDto> cards = await mediaContext
            .Artists.AsNoTracking()
            .ForUser(userId)
            .Where(artist =>
                (letter == "_" || letter == "#")
                    ? !AlphaLetters.Any(p =>
                        (artist.TitleSort ?? artist.Name).ToLower().StartsWith(p)
                    )
                    : (artist.TitleSort ?? artist.Name).ToLower().StartsWith(letter.ToLower())
            )
            .Where(artist =>
                artist.ArtistTrack.AsQueryable().Any(TrackAvailability.ArtistTrackIsPlayable)
            )
            .OrderBy(artist => artist.TitleSort ?? artist.Name)
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
                TrackCount = artist
                    .ArtistTrack.AsQueryable()
                    .Count(TrackAvailability.ArtistTrackIsPlayable),
                ThumbImagePath = artist
                    .Images.Where(image => image.Type == "thumb")
                    .Select(image => image.FilePath)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return cards
            .DistinctBy(c => c.Id)
            .DistinctBy(c => c.Name.Trim().ToLowerInvariant())
            .ToList();
    }

    /// <summary>
    /// Returns every artist in the library as cards, ordered by name. Used by
    /// the TV lolomo layout to build one carousel per first-letter bucket in
    /// a single query instead of 27 round-trips.
    /// </summary>
    public async Task<List<ArtistCardDto>> GetAllArtistCardsAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        List<ArtistCardDto> cards = await mediaContext
            .Artists.AsNoTracking()
            .ForUser(userId)
            .Where(artist =>
                artist.ArtistTrack.AsQueryable().Any(TrackAvailability.ArtistTrackIsPlayable)
            )
            .OrderBy(artist => artist.TitleSort ?? artist.Name)
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
                TrackCount = artist
                    .ArtistTrack.AsQueryable()
                    .Count(TrackAvailability.ArtistTrackIsPlayable),
                ThumbImagePath = artist
                    .Images.Where(image => image.Type == "thumb")
                    .Select(image => image.FilePath)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return cards
            .DistinctBy(c => c.Id)
            .DistinctBy(c => c.Name.Trim().ToLowerInvariant())
            .ToList();
    }

    public async Task<List<ArtistCardDto>> GetLatestArtistCardsAsync(
        int take = 36,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Artists.AsNoTracking()
            .Where(artist =>
                !string.IsNullOrEmpty(artist.Cover)
                && artist.ArtistTrack.AsQueryable().Any(TrackAvailability.ArtistTrackIsPlayable)
            )
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
                TrackCount = artist
                    .ArtistTrack.AsQueryable()
                    .Count(TrackAvailability.ArtistTrackIsPlayable),
                ThumbImagePath = artist
                    .Images.Where(image => image.Type == "thumb")
                    .Select(image => image.FilePath)
                    .FirstOrDefault(),
            })
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<List<ArtistCardDto>> GetFavoriteArtistCardsAsync(
        Guid userId,
        int take = 36,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
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
                TrackCount = artistUser
                    .Artist.ArtistTrack.AsQueryable()
                    .Count(TrackAvailability.ArtistTrackIsPlayable),
                ThumbImagePath = artistUser
                    .Artist.Images.Where(image => image.Type == "thumb")
                    .Select(image => image.FilePath)
                    .FirstOrDefault(),
            })
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<List<ArtistCardDto>> GetArtistCardsByIdsAsync(
        List<Guid> artistIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Artists.AsNoTracking()
            .Where(artist => artistIds.Contains(artist.Id))
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
                TrackCount = artist
                    .ArtistTrack.AsQueryable()
                    .Count(TrackAvailability.ArtistTrackIsPlayable),
                ThumbImagePath = artist
                    .Images.Where(image => image.Type == "thumb")
                    .Select(image => image.FilePath)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
    }

    #endregion
}
