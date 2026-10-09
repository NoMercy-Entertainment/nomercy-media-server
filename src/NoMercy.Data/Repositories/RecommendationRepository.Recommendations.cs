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
    public async Task<List<RecommendationCandidateDto>> GetUnownedMovieRecommendationsAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        // Step 1: Group server-side for IDs only (avoids SQL APPLY)
        // Use NOT EXISTS against Movies table instead of ToId==null (ToId may not be set for older data)
        List<int> mediaIds = await context
            .Recommendations.AsNoTracking()
            .Where(r => r.MovieFromId != null)
            .Where(r => r.MovieFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(r =>
                !context.Movies.Any(m =>
                    m.Id == r.MediaId && m.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Select(r => r.MediaId)
            .Distinct()
            .ToListAsync(ct);

        if (mediaIds.Count == 0)
            return [];

        // Step 2: Fetch metadata for each distinct MediaId
        Dictionary<int, RecommendationCandidateDto> metadataMap = await context
            .Recommendations.AsNoTracking()
            .Where(r => mediaIds.Contains(r.MediaId) && r.MovieFromId != null)
            .Where(r => r.MovieFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(r => new
            {
                r.MediaId,
                r.Title,
                r.TitleSort,
                r.Overview,
                r.Poster,
                r.Backdrop,
                r._colorPalette,
                r.MovieFromId,
            })
            .ToListAsync(ct)
            .ContinueWith(
                t =>
                    t.Result.GroupBy(r => r.MediaId)
                        .ToDictionary(
                            g => g.Key,
                            g =>
                            {
                                var first = g.First();
                                return new RecommendationCandidateDto
                                {
                                    MediaId = g.Key,
                                    Title = first.Title,
                                    TitleSort = first.TitleSort,
                                    Overview = first.Overview,
                                    Poster = first.Poster,
                                    Backdrop = first.Backdrop,
                                    ColorPalette = first._colorPalette.OrEmpty(),
                                    MediaType = MediaTypes.MovieMediaType,
                                    SourceCount = g.Select(r => r.MovieFromId).Distinct().Count(),
                                    SourceIds = [.. g.Select(r => r.MovieFromId!.Value).Distinct()],
                                };
                            }
                        ),
                ct
            );

        return [.. metadataMap.Values];
    }

    public async Task<List<RecommendationCandidateDto>> GetUnownedTvRecommendationsAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<int> mediaIds = await context
            .Recommendations.AsNoTracking()
            .Where(r => r.TvFromId != null)
            .Where(r => r.TvFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(r => r.TvFrom!.MediaType != MediaTypes.AnimeMediaType)
            .Where(r =>
                !context.Tvs.Any(t =>
                    t.Id == r.MediaId && t.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Select(r => r.MediaId)
            .Distinct()
            .ToListAsync(ct);

        if (mediaIds.Count == 0)
            return [];

        Dictionary<int, RecommendationCandidateDto> metadataMap = await context
            .Recommendations.AsNoTracking()
            .Where(r => mediaIds.Contains(r.MediaId) && r.TvFromId != null)
            .Where(r => r.TvFrom!.MediaType != MediaTypes.AnimeMediaType)
            .Where(r => r.TvFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(r => new
            {
                r.MediaId,
                r.Title,
                r.TitleSort,
                r.Overview,
                r.Poster,
                r.Backdrop,
                r._colorPalette,
                r.TvFromId,
            })
            .ToListAsync(ct)
            .ContinueWith(
                t =>
                    t.Result.GroupBy(r => r.MediaId)
                        .ToDictionary(
                            g => g.Key,
                            g =>
                            {
                                var first = g.First();
                                return new RecommendationCandidateDto
                                {
                                    MediaId = g.Key,
                                    Title = first.Title,
                                    TitleSort = first.TitleSort,
                                    Overview = first.Overview,
                                    Poster = first.Poster,
                                    Backdrop = first.Backdrop,
                                    ColorPalette = first._colorPalette.OrEmpty(),
                                    MediaType = MediaTypes.TvMediaType,
                                    SourceCount = g.Select(r => r.TvFromId).Distinct().Count(),
                                    SourceIds = [.. g.Select(r => r.TvFromId!.Value).Distinct()],
                                };
                            }
                        ),
                ct
            );

        return [.. metadataMap.Values];
    }

    public async Task<List<RecommendationCandidateDto>> GetUnownedAnimeRecommendationsAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<int> mediaIds = await context
            .Recommendations.AsNoTracking()
            .Where(r => r.TvFromId != null)
            .Where(r => r.TvFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(r => r.TvFrom!.MediaType == MediaTypes.AnimeMediaType)
            .Where(r =>
                !context.Tvs.Any(t =>
                    t.Id == r.MediaId && t.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Select(r => r.MediaId)
            .Distinct()
            .ToListAsync(ct);

        if (mediaIds.Count == 0)
            return [];

        Dictionary<int, RecommendationCandidateDto> metadataMap = await context
            .Recommendations.AsNoTracking()
            .Where(r => mediaIds.Contains(r.MediaId) && r.TvFromId != null)
            .Where(r => r.TvFrom!.MediaType == MediaTypes.AnimeMediaType)
            .Where(r => r.TvFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(r => new
            {
                r.MediaId,
                r.Title,
                r.TitleSort,
                r.Overview,
                r.Poster,
                r.Backdrop,
                r._colorPalette,
                r.TvFromId,
            })
            .ToListAsync(ct)
            .ContinueWith(
                t =>
                    t.Result.GroupBy(r => r.MediaId)
                        .ToDictionary(
                            g => g.Key,
                            g =>
                            {
                                var first = g.First();
                                return new RecommendationCandidateDto
                                {
                                    MediaId = g.Key,
                                    Title = first.Title,
                                    TitleSort = first.TitleSort,
                                    Overview = first.Overview,
                                    Poster = first.Poster,
                                    Backdrop = first.Backdrop,
                                    ColorPalette = first._colorPalette.OrEmpty(),
                                    MediaType = MediaTypes.AnimeMediaType,
                                    SourceCount = g.Select(r => r.TvFromId).Distinct().Count(),
                                    SourceIds = [.. g.Select(r => r.TvFromId!.Value).Distinct()],
                                };
                            }
                        ),
                ct
            );

        return [.. metadataMap.Values];
    }

    public async Task<List<RecommendationCandidateDto>> GetUnownedMovieSimilarAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<int> mediaIds = await context
            .Similar.AsNoTracking()
            .Where(s => s.MovieFromId != null)
            .Where(s => s.MovieFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(s =>
                !context.Movies.Any(m =>
                    m.Id == s.MediaId && m.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Select(s => s.MediaId)
            .Distinct()
            .ToListAsync(ct);

        if (mediaIds.Count == 0)
            return [];

        Dictionary<int, RecommendationCandidateDto> metadataMap = await context
            .Similar.AsNoTracking()
            .Where(s => mediaIds.Contains(s.MediaId) && s.MovieFromId != null)
            .Where(s => s.MovieFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(s => new
            {
                s.MediaId,
                s.Title,
                s.TitleSort,
                s.Overview,
                s.Poster,
                s.Backdrop,
                s._colorPalette,
                s.MovieFromId,
            })
            .ToListAsync(ct)
            .ContinueWith(
                t =>
                    t.Result.GroupBy(s => s.MediaId)
                        .ToDictionary(
                            g => g.Key,
                            g =>
                            {
                                var first = g.First();
                                return new RecommendationCandidateDto
                                {
                                    MediaId = g.Key,
                                    Title = first.Title,
                                    TitleSort = first.TitleSort,
                                    Overview = first.Overview,
                                    Poster = first.Poster,
                                    Backdrop = first.Backdrop,
                                    ColorPalette = first._colorPalette.OrEmpty(),
                                    MediaType = MediaTypes.MovieMediaType,
                                    SourceCount = g.Select(s => s.MovieFromId).Distinct().Count(),
                                    SourceIds = [.. g.Select(s => s.MovieFromId!.Value).Distinct()],
                                };
                            }
                        ),
                ct
            );

        return [.. metadataMap.Values];
    }

    public async Task<List<RecommendationCandidateDto>> GetUnownedTvSimilarAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<int> mediaIds = await context
            .Similar.AsNoTracking()
            .Where(s => s.TvFromId != null)
            .Where(s => s.TvFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(s => s.TvFrom!.MediaType != MediaTypes.AnimeMediaType)
            .Where(s =>
                !context.Tvs.Any(t =>
                    t.Id == s.MediaId && t.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Select(s => s.MediaId)
            .Distinct()
            .ToListAsync(ct);

        if (mediaIds.Count == 0)
            return [];

        Dictionary<int, RecommendationCandidateDto> metadataMap = await context
            .Similar.AsNoTracking()
            .Where(s => mediaIds.Contains(s.MediaId) && s.TvFromId != null)
            .Where(s => s.TvFrom!.MediaType != MediaTypes.AnimeMediaType)
            .Where(s => s.TvFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(s => new
            {
                s.MediaId,
                s.Title,
                s.TitleSort,
                s.Overview,
                s.Poster,
                s.Backdrop,
                s._colorPalette,
                s.TvFromId,
            })
            .ToListAsync(ct)
            .ContinueWith(
                t =>
                    t.Result.GroupBy(s => s.MediaId)
                        .ToDictionary(
                            g => g.Key,
                            g =>
                            {
                                var first = g.First();
                                return new RecommendationCandidateDto
                                {
                                    MediaId = g.Key,
                                    Title = first.Title,
                                    TitleSort = first.TitleSort,
                                    Overview = first.Overview,
                                    Poster = first.Poster,
                                    Backdrop = first.Backdrop,
                                    ColorPalette = first._colorPalette.OrEmpty(),
                                    MediaType = MediaTypes.TvMediaType,
                                    SourceCount = g.Select(s => s.TvFromId).Distinct().Count(),
                                    SourceIds = [.. g.Select(s => s.TvFromId!.Value).Distinct()],
                                };
                            }
                        ),
                ct
            );

        return [.. metadataMap.Values];
    }

    public async Task<List<RecommendationCandidateDto>> GetUnownedAnimeSimilarAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<int> mediaIds = await context
            .Similar.AsNoTracking()
            .Where(s => s.TvFromId != null)
            .Where(s => s.TvFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(s => s.TvFrom!.MediaType == MediaTypes.AnimeMediaType)
            .Where(s =>
                !context.Tvs.Any(t =>
                    t.Id == s.MediaId && t.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Select(s => s.MediaId)
            .Distinct()
            .ToListAsync(ct);

        if (mediaIds.Count == 0)
            return [];

        Dictionary<int, RecommendationCandidateDto> metadataMap = await context
            .Similar.AsNoTracking()
            .Where(s => mediaIds.Contains(s.MediaId) && s.TvFromId != null)
            .Where(s => s.TvFrom!.MediaType == MediaTypes.AnimeMediaType)
            .Where(s => s.TvFrom!.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(s => new
            {
                s.MediaId,
                s.Title,
                s.TitleSort,
                s.Overview,
                s.Poster,
                s.Backdrop,
                s._colorPalette,
                s.TvFromId,
            })
            .ToListAsync(ct)
            .ContinueWith(
                t =>
                    t.Result.GroupBy(s => s.MediaId)
                        .ToDictionary(
                            g => g.Key,
                            g =>
                            {
                                var first = g.First();
                                return new RecommendationCandidateDto
                                {
                                    MediaId = g.Key,
                                    Title = first.Title,
                                    TitleSort = first.TitleSort,
                                    Overview = first.Overview,
                                    Poster = first.Poster,
                                    Backdrop = first.Backdrop,
                                    ColorPalette = first._colorPalette.OrEmpty(),
                                    MediaType = MediaTypes.AnimeMediaType,
                                    SourceCount = g.Select(s => s.TvFromId).Distinct().Count(),
                                    SourceIds = [.. g.Select(s => s.TvFromId!.Value).Distinct()],
                                };
                            }
                        ),
                ct
            );

        return [.. metadataMap.Values];
    }
}
