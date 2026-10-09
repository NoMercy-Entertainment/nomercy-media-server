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
    public async Task<List<RecommendationCandidateDto>> GetKeywordCrossTypeTvCandidatesAsync(
        Guid userId,
        Dictionary<int, List<int>> movieKeywordMap,
        int minSharedKeywords = 3,
        int maxCandidates = 100,
        int maxKeywordFrequency = 50,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        if (movieKeywordMap.Count == 0)
            return [];

        HashSet<int> ownedMovieKeywordIds = [.. movieKeywordMap.Values.SelectMany(kws => kws)];

        if (ownedMovieKeywordIds.Count == 0)
            return [];

        // Filter out overly common keywords — generic tags like "animation" or "cat" match too many items
        HashSet<int> commonKeywordIds =
        [
            .. await context
                .KeywordTv.AsNoTracking()
                .Where(kt => ownedMovieKeywordIds.Contains(kt.KeywordId))
                .GroupBy(kt => kt.KeywordId)
                .Where(g => g.Count() > maxKeywordFrequency)
                .Select(g => g.Key)
                .ToListAsync(ct),
        ];

        HashSet<int> specificKeywordIds =
        [
            .. ownedMovieKeywordIds.Where(id => !commonKeywordIds.Contains(id)),
        ];

        if (specificKeywordIds.Count == 0)
            return [];

        // Step 1: Flat server-side query — find KeywordTv rows matching specific movie keywords on unowned TV shows (excluding anime)
        var keywordTvRows = await context
            .KeywordTv.AsNoTracking()
            .Where(kt => specificKeywordIds.Contains(kt.KeywordId))
            .Where(kt =>
                context.Tvs.Any(t => t.Id == kt.TvId && t.MediaType != MediaTypes.AnimeMediaType)
            )
            .Where(kt =>
                !context.Tvs.Any(t =>
                    t.Id == kt.TvId && t.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Select(kt => new { kt.TvId, kt.KeywordId })
            .ToListAsync(ct);

        // Step 2: Client-side grouping — filter by minimum shared keyword count
        var tvKeywordGroups = keywordTvRows
            .GroupBy(r => r.TvId)
            .Where(g => g.Count() >= minSharedKeywords)
            .OrderByDescending(g => g.Count())
            .Take(maxCandidates)
            .ToList();

        if (tvKeywordGroups.Count == 0)
            return [];

        // Step 3: Fetch TV metadata for qualifying shows
        List<int> qualifyingTvIds = [.. tvKeywordGroups.Select(g => g.Key)];

        var tvMetadata = await context
            .Tvs.AsNoTracking()
            .Where(t => qualifyingTvIds.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.TitleSort,
                t.Overview,
                t.Poster,
                t.Backdrop,
                t._colorPalette,
            })
            .ToListAsync(ct);

        Dictionary<
            int,
            (
                string Title,
                string TitleSort,
                string? Overview,
                string? Poster,
                string? Backdrop,
                string? Palette
            )
        > metaMap = tvMetadata.ToDictionary(
            t => t.Id,
            t => (t.Title, t.TitleSort, t.Overview, t.Poster, t.Backdrop, t._colorPalette)
        );

        // Step 4: Build candidates with reverse-mapped source IDs
        return
        [
            .. tvKeywordGroups
                .Where(g => metaMap.ContainsKey(g.Key))
                .Select(g =>
                {
                    (
                        string Title,
                        string TitleSort,
                        string? Overview,
                        string? Poster,
                        string? Backdrop,
                        string? Palette
                    ) meta = metaMap[g.Key];
                    HashSet<int> sharedKeywordIds = [.. g.Select(r => r.KeywordId)];
                    List<int> sourceMovieIds =
                    [
                        .. movieKeywordMap
                            .Where(kv => kv.Value.Any(kw => sharedKeywordIds.Contains(kw)))
                            .Select(kv => kv.Key)
                            .Distinct(),
                    ];

                    return new RecommendationCandidateDto
                    {
                        MediaId = g.Key,
                        Title = meta.Title,
                        TitleSort = meta.TitleSort,
                        Overview = meta.Overview,
                        Poster = meta.Poster,
                        Backdrop = meta.Backdrop,
                        ColorPalette = meta.Palette.OrEmpty(),
                        MediaType = MediaTypes.TvMediaType,
                        SourceMediaType = MediaTypes.MovieMediaType,
                        SourceCount = sourceMovieIds.Count,
                        SourceIds = sourceMovieIds,
                    };
                }),
        ];
    }

    public async Task<List<RecommendationCandidateDto>> GetKeywordCrossTypeAnimeCandidatesAsync(
        Guid userId,
        Dictionary<int, List<int>> movieKeywordMap,
        int minSharedKeywords = 3,
        int maxCandidates = 100,
        int maxKeywordFrequency = 50,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        if (movieKeywordMap.Count == 0)
            return [];

        HashSet<int> ownedMovieKeywordIds = [.. movieKeywordMap.Values.SelectMany(kws => kws)];

        if (ownedMovieKeywordIds.Count == 0)
            return [];

        HashSet<int> commonKeywordIds =
        [
            .. await context
                .KeywordTv.AsNoTracking()
                .Where(kt => ownedMovieKeywordIds.Contains(kt.KeywordId))
                .GroupBy(kt => kt.KeywordId)
                .Where(g => g.Count() > maxKeywordFrequency)
                .Select(g => g.Key)
                .ToListAsync(ct),
        ];

        HashSet<int> specificKeywordIds =
        [
            .. ownedMovieKeywordIds.Where(id => !commonKeywordIds.Contains(id)),
        ];

        if (specificKeywordIds.Count == 0)
            return [];

        var keywordTvRows = await context
            .KeywordTv.AsNoTracking()
            .Where(kt => specificKeywordIds.Contains(kt.KeywordId))
            .Where(kt =>
                context.Tvs.Any(t => t.Id == kt.TvId && t.MediaType == MediaTypes.AnimeMediaType)
            )
            .Where(kt =>
                !context.Tvs.Any(t =>
                    t.Id == kt.TvId && t.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Select(kt => new { kt.TvId, kt.KeywordId })
            .ToListAsync(ct);

        var tvKeywordGroups = keywordTvRows
            .GroupBy(r => r.TvId)
            .Where(g => g.Count() >= minSharedKeywords)
            .OrderByDescending(g => g.Count())
            .Take(maxCandidates)
            .ToList();

        if (tvKeywordGroups.Count == 0)
            return [];

        List<int> qualifyingTvIds = [.. tvKeywordGroups.Select(g => g.Key)];

        var tvMetadata = await context
            .Tvs.AsNoTracking()
            .Where(t => qualifyingTvIds.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.TitleSort,
                t.Overview,
                t.Poster,
                t.Backdrop,
                t._colorPalette,
            })
            .ToListAsync(ct);

        Dictionary<
            int,
            (
                string Title,
                string TitleSort,
                string? Overview,
                string? Poster,
                string? Backdrop,
                string? Palette
            )
        > metaMap = tvMetadata.ToDictionary(
            t => t.Id,
            t => (t.Title, t.TitleSort, t.Overview, t.Poster, t.Backdrop, t._colorPalette)
        );

        return
        [
            .. tvKeywordGroups
                .Where(g => metaMap.ContainsKey(g.Key))
                .Select(g =>
                {
                    (
                        string Title,
                        string TitleSort,
                        string? Overview,
                        string? Poster,
                        string? Backdrop,
                        string? Palette
                    ) meta = metaMap[g.Key];
                    HashSet<int> sharedKeywordIds = [.. g.Select(r => r.KeywordId)];
                    List<int> sourceMovieIds =
                    [
                        .. movieKeywordMap
                            .Where(kv => kv.Value.Any(kw => sharedKeywordIds.Contains(kw)))
                            .Select(kv => kv.Key)
                            .Distinct(),
                    ];

                    return new RecommendationCandidateDto
                    {
                        MediaId = g.Key,
                        Title = meta.Title,
                        TitleSort = meta.TitleSort,
                        Overview = meta.Overview,
                        Poster = meta.Poster,
                        Backdrop = meta.Backdrop,
                        ColorPalette = meta.Palette.OrEmpty(),
                        MediaType = MediaTypes.AnimeMediaType,
                        SourceMediaType = MediaTypes.MovieMediaType,
                        SourceCount = sourceMovieIds.Count,
                        SourceIds = sourceMovieIds,
                    };
                }),
        ];
    }

    public async Task<List<RecommendationCandidateDto>> GetKeywordCrossTypeMovieCandidatesAsync(
        Guid userId,
        Dictionary<int, List<int>> tvKeywordMap,
        int minSharedKeywords = 3,
        int maxCandidates = 100,
        int maxKeywordFrequency = 50,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        if (tvKeywordMap.Count == 0)
            return [];

        HashSet<int> ownedTvKeywordIds = [.. tvKeywordMap.Values.SelectMany(kws => kws)];

        if (ownedTvKeywordIds.Count == 0)
            return [];

        // Filter out overly common keywords — generic tags match too many items
        HashSet<int> commonKeywordIds =
        [
            .. await context
                .KeywordMovie.AsNoTracking()
                .Where(km => ownedTvKeywordIds.Contains(km.KeywordId))
                .GroupBy(km => km.KeywordId)
                .Where(g => g.Count() > maxKeywordFrequency)
                .Select(g => g.Key)
                .ToListAsync(ct),
        ];

        HashSet<int> specificKeywordIds =
        [
            .. ownedTvKeywordIds.Where(id => !commonKeywordIds.Contains(id)),
        ];

        if (specificKeywordIds.Count == 0)
            return [];

        // Step 1: Flat server-side query — find KeywordMovie rows matching specific TV keywords on unowned movies
        var keywordMovieRows = await context
            .KeywordMovie.AsNoTracking()
            .Where(km => specificKeywordIds.Contains(km.KeywordId))
            .Where(km =>
                !context.Movies.Any(m =>
                    m.Id == km.MovieId && m.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Select(km => new { km.MovieId, km.KeywordId })
            .ToListAsync(ct);

        // Step 2: Client-side grouping — filter by minimum shared keyword count
        var movieKeywordGroups = keywordMovieRows
            .GroupBy(r => r.MovieId)
            .Where(g => g.Count() >= minSharedKeywords)
            .OrderByDescending(g => g.Count())
            .Take(maxCandidates)
            .ToList();

        if (movieKeywordGroups.Count == 0)
            return [];

        // Step 3: Fetch movie metadata for qualifying movies
        List<int> qualifyingMovieIds = [.. movieKeywordGroups.Select(g => g.Key)];

        var movieMetadata = await context
            .Movies.AsNoTracking()
            .Where(m => qualifyingMovieIds.Contains(m.Id))
            .Select(m => new
            {
                m.Id,
                m.Title,
                m.TitleSort,
                m.Overview,
                m.Poster,
                m.Backdrop,
                m._colorPalette,
            })
            .ToListAsync(ct);

        Dictionary<
            int,
            (
                string Title,
                string TitleSort,
                string? Overview,
                string? Poster,
                string? Backdrop,
                string? Palette
            )
        > metaMap = movieMetadata.ToDictionary(
            m => m.Id,
            m => (m.Title, m.TitleSort, m.Overview, m.Poster, m.Backdrop, m._colorPalette)
        );

        // Step 4: Build candidates with reverse-mapped source IDs
        return
        [
            .. movieKeywordGroups
                .Where(g => metaMap.ContainsKey(g.Key))
                .Select(g =>
                {
                    (
                        string Title,
                        string TitleSort,
                        string? Overview,
                        string? Poster,
                        string? Backdrop,
                        string? Palette
                    ) meta = metaMap[g.Key];
                    HashSet<int> sharedKeywordIds = [.. g.Select(r => r.KeywordId)];
                    List<int> sourceTvIds =
                    [
                        .. tvKeywordMap
                            .Where(kv => kv.Value.Any(kw => sharedKeywordIds.Contains(kw)))
                            .Select(kv => kv.Key)
                            .Distinct(),
                    ];

                    return new RecommendationCandidateDto
                    {
                        MediaId = g.Key,
                        Title = meta.Title,
                        TitleSort = meta.TitleSort,
                        Overview = meta.Overview,
                        Poster = meta.Poster,
                        Backdrop = meta.Backdrop,
                        ColorPalette = meta.Palette.OrEmpty(),
                        MediaType = MediaTypes.MovieMediaType,
                        SourceMediaType = MediaTypes.TvMediaType,
                        SourceCount = sourceTvIds.Count,
                        SourceIds = sourceTvIds,
                    };
                }),
        ];
    }
}
