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
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Data.Repositories;

public partial class RecommendationRepository
{
    public async Task<Dictionary<int, List<int>>> GetGenresForMovieIdsAsync(
        List<int> movieIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        if (movieIds.Count == 0)
            return new();

        return await context
            .GenreMovie.AsNoTracking()
            .Where(gm => movieIds.Contains(gm.MovieId))
            .GroupBy(gm => gm.MovieId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(gm => gm.GenreId).ToList(), ct);
    }

    public async Task<Dictionary<int, List<int>>> GetGenresForTvIdsAsync(
        List<int> tvIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        if (tvIds.Count == 0)
            return new();

        return await context
            .GenreTv.AsNoTracking()
            .Where(gt => tvIds.Contains(gt.TvId))
            .GroupBy(gt => gt.TvId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(gt => gt.GenreId).ToList(), ct);
    }

    public async Task<(List<Movie> Movies, string? ColorPalette)> GetSourceMoviesForMediaAsync(
        Guid userId,
        int mediaId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        // Get distinct source movie IDs and grab color palette from the same query
        var recRows = await context
            .Recommendations.AsNoTracking()
            .Where(r => r.MediaId == mediaId && r.MovieFromId != null)
            .Where(r => r.MovieFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(r => new { SourceId = r.MovieFromId!.Value, r._colorPalette })
            .ToListAsync(ct);

        var simRows = await context
            .Similar.AsNoTracking()
            .Where(s => s.MediaId == mediaId && s.MovieFromId != null)
            .Where(s => s.MovieFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(s => new { SourceId = s.MovieFromId!.Value, s._colorPalette })
            .ToListAsync(ct);

        var allRows = recRows.Concat(simRows).ToList();
        string? colorPalette = allRows
            .FirstOrDefault(r => !string.IsNullOrEmpty(r._colorPalette))
            ?._colorPalette;
        List<int> sourceIds = allRows.Select(r => r.SourceId).Distinct().ToList();

        if (sourceIds.Count == 0)
            return ([], colorPalette);

        List<Movie> movies = await context
            .Movies.AsNoTracking()
            .Where(m => sourceIds.Contains(m.Id))
            .Where(m => m.VideoFiles.Any())
            .Include(m =>
                m.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .OrderByDescending(i => i.VoteAverage)
                    .ThenBy(i => i.Id)
            )
            .Include(m => m.VideoFiles)
            .Include(m => m.KeywordMovies)
                .ThenInclude(km => km.Keyword)
            .ToListAsync(ct);

        return (movies, colorPalette);
    }

    public async Task<(List<Tv> TvShows, string? ColorPalette)> GetSourceTvShowsForMediaAsync(
        Guid userId,
        int mediaId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        var recRows = await context
            .Recommendations.AsNoTracking()
            .Where(r => r.MediaId == mediaId && r.TvFromId != null)
            .Where(r => r.TvFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(r => new { SourceId = r.TvFromId!.Value, r._colorPalette })
            .ToListAsync(ct);

        var simRows = await context
            .Similar.AsNoTracking()
            .Where(s => s.MediaId == mediaId && s.TvFromId != null)
            .Where(s => s.TvFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(s => new { SourceId = s.TvFromId!.Value, s._colorPalette })
            .ToListAsync(ct);

        var allRows = recRows.Concat(simRows).ToList();
        string? colorPalette = allRows
            .FirstOrDefault(r => !string.IsNullOrEmpty(r._colorPalette))
            ?._colorPalette;
        List<int> sourceIds = allRows.Select(r => r.SourceId).Distinct().ToList();

        if (sourceIds.Count == 0)
            return ([], colorPalette);

        List<Tv> tvShows = await context
            .Tvs.AsNoTracking()
            .Where(t => sourceIds.Contains(t.Id))
            .Where(t => t.Episodes.Any(e => e.SeasonNumber > 0 && e.VideoFiles.Any()))
            .Include(t =>
                t.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .OrderByDescending(i => i.VoteAverage)
                    .ThenBy(i => i.Id)
            )
            .Include(t => t.Episodes)
                .ThenInclude(e => e.VideoFiles)
            .Include(t => t.KeywordTvs)
                .ThenInclude(kt => kt.Keyword)
            .ToListAsync(ct);

        return (tvShows, colorPalette);
    }

    public async Task<List<Movie>> GetKeywordMovieSourcesForMovieAsync(
        Guid userId,
        int movieId,
        HashSet<int> excludeIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<int> targetKeywordIds = await context
            .KeywordMovie.AsNoTracking()
            .Where(km => km.MovieId == movieId)
            .Select(km => km.KeywordId)
            .ToListAsync(ct);

        if (targetKeywordIds.Count == 0)
            return [];

        List<int> matchingMovieIds = await context
            .KeywordMovie.AsNoTracking()
            .Where(km => targetKeywordIds.Contains(km.KeywordId))
            .Where(km => km.Movie.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(km => km.Movie.VideoFiles.Any())
            .Where(km => !excludeIds.Contains(km.MovieId))
            .Select(km => km.MovieId)
            .Distinct()
            .ToListAsync(ct);

        if (matchingMovieIds.Count == 0)
            return [];

        return await context
            .Movies.AsNoTracking()
            .Where(m => matchingMovieIds.Contains(m.Id))
            .Include(m =>
                m.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .OrderByDescending(i => i.VoteAverage)
                    .ThenBy(i => i.Id)
            )
            .Include(m => m.VideoFiles)
            .Include(m => m.KeywordMovies)
                .ThenInclude(km => km.Keyword)
            .ToListAsync(ct);
    }

    public async Task<List<Tv>> GetKeywordTvSourcesForTvAsync(
        Guid userId,
        int tvId,
        HashSet<int> excludeIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<int> targetKeywordIds = await context
            .KeywordTv.AsNoTracking()
            .Where(kt => kt.TvId == tvId)
            .Select(kt => kt.KeywordId)
            .ToListAsync(ct);

        if (targetKeywordIds.Count == 0)
            return [];

        List<int> matchingTvIds = await context
            .KeywordTv.AsNoTracking()
            .Where(kt => targetKeywordIds.Contains(kt.KeywordId))
            .Where(kt => kt.Tv.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(kt => kt.Tv.Episodes.Any(e => e.SeasonNumber > 0 && e.VideoFiles.Any()))
            .Where(kt => !excludeIds.Contains(kt.TvId))
            .Select(kt => kt.TvId)
            .Distinct()
            .ToListAsync(ct);

        if (matchingTvIds.Count == 0)
            return [];

        return await context
            .Tvs.AsNoTracking()
            .Where(t => matchingTvIds.Contains(t.Id))
            .Include(t =>
                t.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .OrderByDescending(i => i.VoteAverage)
                    .ThenBy(i => i.Id)
            )
            .Include(t => t.Episodes)
                .ThenInclude(e => e.VideoFiles)
            .Include(t => t.KeywordTvs)
                .ThenInclude(kt => kt.Keyword)
            .ToListAsync(ct);
    }

    public async Task<List<Movie>> GetCrossTypeMovieSourcesForTvAsync(
        Guid userId,
        int tvId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<int> tvKeywordIds = await context
            .KeywordTv.AsNoTracking()
            .Where(kt => kt.TvId == tvId)
            .Select(kt => kt.KeywordId)
            .ToListAsync(ct);

        if (tvKeywordIds.Count == 0)
            return [];

        List<int> movieIds = await context
            .KeywordMovie.AsNoTracking()
            .Where(km => tvKeywordIds.Contains(km.KeywordId))
            .Where(km => km.Movie.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(km => km.Movie.VideoFiles.Any())
            .Select(km => km.MovieId)
            .Distinct()
            .ToListAsync(ct);

        if (movieIds.Count == 0)
            return [];

        return await context
            .Movies.AsNoTracking()
            .Where(m => movieIds.Contains(m.Id))
            .Include(m =>
                m.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .OrderByDescending(i => i.VoteAverage)
                    .ThenBy(i => i.Id)
            )
            .Include(m => m.VideoFiles)
            .Include(m => m.KeywordMovies)
                .ThenInclude(km => km.Keyword)
            .ToListAsync(ct);
    }

    public async Task<List<Tv>> GetCrossTypeTvSourcesForMovieAsync(
        Guid userId,
        int movieId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<int> movieKeywordIds = await context
            .KeywordMovie.AsNoTracking()
            .Where(km => km.MovieId == movieId)
            .Select(km => km.KeywordId)
            .ToListAsync(ct);

        if (movieKeywordIds.Count == 0)
            return [];

        List<int> tvIds = await context
            .KeywordTv.AsNoTracking()
            .Where(kt => movieKeywordIds.Contains(kt.KeywordId))
            .Where(kt => kt.Tv.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(kt => kt.Tv.Episodes.Any(e => e.SeasonNumber > 0 && e.VideoFiles.Any()))
            .Select(kt => kt.TvId)
            .Distinct()
            .ToListAsync(ct);

        if (tvIds.Count == 0)
            return [];

        return await context
            .Tvs.AsNoTracking()
            .Where(t => tvIds.Contains(t.Id))
            .Include(t =>
                t.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .OrderByDescending(i => i.VoteAverage)
                    .ThenBy(i => i.Id)
            )
            .Include(t => t.Episodes)
                .ThenInclude(e => e.VideoFiles)
            .Include(t => t.KeywordTvs)
                .ThenInclude(kt => kt.Keyword)
            .ToListAsync(ct);
    }
}
