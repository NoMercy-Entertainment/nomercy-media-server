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
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Data.Repositories;

public partial class RecommendationRepository
{
    public async Task<List<UserAffinitySourceDto>> GetUserMovieAffinityDataAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        // Fetch flat data without nested collection projections to avoid SQL APPLY
        var movies = await context
            .Movies.AsNoTracking()
            .Where(m => m.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(m => m.VideoFiles.Any())
            .Select(m => new
            {
                m.Id,
                m.Title,
                m.Poster,
                m._colorPalette,
                m.Runtime,
                Rating = m
                    .UserData.Where(ud => ud.UserId == userId && ud.Rating != null)
                    .Select(ud => ud.Rating)
                    .FirstOrDefault(),
                TimeWatched = m
                    .UserData.Where(ud => ud.UserId == userId)
                    .OrderByDescending(ud => ud.Time)
                    .ThenByDescending(ud => ud.Id)
                    .Select(ud => ud.Time)
                    .FirstOrDefault(),
                IsFavorited = m.MovieUser.Any(mu => mu.UserId == userId),
            })
            .ToListAsync(ct);

        if (movies.Count == 0)
            return [];

        List<int> movieIds = movies.Select(m => m.Id).ToList();

        // Fetch genre and keyword mappings separately
        Dictionary<int, List<int>> genreMap = await context
            .GenreMovie.AsNoTracking()
            .Where(gm => movieIds.Contains(gm.MovieId))
            .GroupBy(gm => gm.MovieId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(gm => gm.GenreId).ToList(), ct);

        Dictionary<int, List<int>> keywordMap = await context
            .KeywordMovie.AsNoTracking()
            .Where(km => movieIds.Contains(km.MovieId))
            .GroupBy(km => km.MovieId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(km => km.KeywordId).ToList(), ct);

        return movies
            .Select(m => new UserAffinitySourceDto
            {
                ItemId = m.Id,
                Title = m.Title,
                Poster = m.Poster,
                ColorPalette = m._colorPalette.OrEmpty(),
                MediaType = MediaTypes.MovieMediaType,
                Rating = m.Rating,
                TimeWatched = m.TimeWatched,
                Duration = m.Runtime != null ? m.Runtime * 60 : null,
                IsFavorited = m.IsFavorited,
                GenreIds = genreMap.GetValueOrDefault(m.Id, []),
                KeywordIds = keywordMap.GetValueOrDefault(m.Id, []),
            })
            .ToList();
    }

    public async Task<List<UserAffinitySourceDto>> GetUserTvAffinityDataAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        var tvShows = await context
            .Tvs.AsNoTracking()
            .Where(t => t.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(t => t.MediaType != MediaTypes.AnimeMediaType)
            .Where(t => t.Episodes.Any(e => e.SeasonNumber > 0 && e.VideoFiles.Any()))
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.Poster,
                t._colorPalette,
                t.Duration,
                Rating = t
                    .UserData.Where(ud => ud.UserId == userId && ud.Rating != null)
                    .Select(ud => ud.Rating)
                    .FirstOrDefault(),
                TimeWatched = t
                    .UserData.Where(ud => ud.UserId == userId)
                    .OrderByDescending(ud => ud.Time)
                    .ThenByDescending(ud => ud.Id)
                    .Select(ud => ud.Time)
                    .FirstOrDefault(),
                IsFavorited = t.TvUser.Any(tu => tu.UserId == userId),
            })
            .ToListAsync(ct);

        if (tvShows.Count == 0)
            return [];

        List<int> tvIds = tvShows.Select(t => t.Id).ToList();

        Dictionary<int, List<int>> genreMap = await context
            .GenreTv.AsNoTracking()
            .Where(gt => tvIds.Contains(gt.TvId))
            .GroupBy(gt => gt.TvId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(gt => gt.GenreId).ToList(), ct);

        Dictionary<int, List<int>> keywordMap = await context
            .KeywordTv.AsNoTracking()
            .Where(kt => tvIds.Contains(kt.TvId))
            .GroupBy(kt => kt.TvId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(kt => kt.KeywordId).ToList(), ct);

        return tvShows
            .Select(t => new UserAffinitySourceDto
            {
                ItemId = t.Id,
                Title = t.Title,
                Poster = t.Poster,
                ColorPalette = t._colorPalette.OrEmpty(),
                MediaType = MediaTypes.TvMediaType,
                Rating = t.Rating,
                TimeWatched = t.TimeWatched,
                Duration = t.Duration,
                IsFavorited = t.IsFavorited,
                GenreIds = genreMap.GetValueOrDefault(t.Id, []),
                KeywordIds = keywordMap.GetValueOrDefault(t.Id, []),
            })
            .ToList();
    }

    public async Task<List<UserAffinitySourceDto>> GetUserAnimeAffinityDataAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        var animeShows = await context
            .Tvs.AsNoTracking()
            .Where(t => t.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(t => t.MediaType == MediaTypes.AnimeMediaType)
            .Where(t => t.Episodes.Any(e => e.SeasonNumber > 0 && e.VideoFiles.Any()))
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.Poster,
                t._colorPalette,
                t.Duration,
                Rating = t
                    .UserData.Where(ud => ud.UserId == userId && ud.Rating != null)
                    .Select(ud => ud.Rating)
                    .FirstOrDefault(),
                TimeWatched = t
                    .UserData.Where(ud => ud.UserId == userId)
                    .OrderByDescending(ud => ud.Time)
                    .ThenByDescending(ud => ud.Id)
                    .Select(ud => ud.Time)
                    .FirstOrDefault(),
                IsFavorited = t.TvUser.Any(tu => tu.UserId == userId),
            })
            .ToListAsync(ct);

        if (animeShows.Count == 0)
            return [];

        List<int> animeIds = animeShows.Select(t => t.Id).ToList();

        Dictionary<int, List<int>> genreMap = await context
            .GenreTv.AsNoTracking()
            .Where(gt => animeIds.Contains(gt.TvId))
            .GroupBy(gt => gt.TvId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(gt => gt.GenreId).ToList(), ct);

        Dictionary<int, List<int>> keywordMap = await context
            .KeywordTv.AsNoTracking()
            .Where(kt => animeIds.Contains(kt.TvId))
            .GroupBy(kt => kt.TvId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(kt => kt.KeywordId).ToList(), ct);

        return animeShows
            .Select(t => new UserAffinitySourceDto
            {
                ItemId = t.Id,
                Title = t.Title,
                Poster = t.Poster,
                ColorPalette = t._colorPalette.OrEmpty(),
                MediaType = MediaTypes.AnimeMediaType,
                Rating = t.Rating,
                TimeWatched = t.TimeWatched,
                Duration = t.Duration,
                IsFavorited = t.IsFavorited,
                GenreIds = genreMap.GetValueOrDefault(t.Id, []),
                KeywordIds = keywordMap.GetValueOrDefault(t.Id, []),
            })
            .ToList();
    }
}
