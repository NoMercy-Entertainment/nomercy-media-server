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

namespace NoMercy.Data.Repositories;

public partial class RecommendationRepository
{
    public async Task<RecommendationDiagnosticsDto> GetDiagnosticsAsync(
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        int animeByLibraryType = await context
            .Tvs.AsNoTracking()
            .CountAsync(t => t.Library.Type == MediaTypes.AnimeMediaType, ct);

        int animeByMediaType = await context
            .Tvs.AsNoTracking()
            .CountAsync(t => t.MediaType == MediaTypes.AnimeMediaType, ct);

        int totalRecsWithTv = await context
            .Recommendations.AsNoTracking()
            .CountAsync(r => r.TvFromId != null, ct);

        int animeRecsByMediaType = await context
            .Recommendations.AsNoTracking()
            .CountAsync(
                r =>
                    r.TvFromId != null
                    && context.Tvs.Any(t =>
                        t.Id == r.TvFromId && t.MediaType == MediaTypes.AnimeMediaType
                    ),
                ct
            );

        int totalSimWithTv = await context
            .Similar.AsNoTracking()
            .CountAsync(s => s.TvFromId != null, ct);

        int animeSimByMediaType = await context
            .Similar.AsNoTracking()
            .CountAsync(
                s =>
                    s.TvFromId != null
                    && context.Tvs.Any(t =>
                        t.Id == s.TvFromId && t.MediaType == MediaTypes.AnimeMediaType
                    ),
                ct
            );

        List<string> libraries = await context
            .Libraries.AsNoTracking()
            .Select(l => l.Title + " (" + l.Type + ")")
            .ToListAsync(ct);

        List<int> sampleAnimeIds = await context
            .Tvs.AsNoTracking()
            .Where(t => t.MediaType == MediaTypes.AnimeMediaType)
            .OrderBy(t => t.Id)
            .Take(5)
            .Select(t => t.Id)
            .ToListAsync(ct);

        int sampleRecsCount =
            sampleAnimeIds.Count > 0
                ? await context
                    .Recommendations.AsNoTracking()
                    .CountAsync(r => sampleAnimeIds.Contains(r.TvFromId!.Value), ct)
                : 0;

        return new()
        {
            Libraries = libraries,
            AnimeByLibraryType = animeByLibraryType,
            AnimeByMediaType = animeByMediaType,
            TotalRecsWithTv = totalRecsWithTv,
            AnimeRecsByMediaType = animeRecsByMediaType,
            TotalSimWithTv = totalSimWithTv,
            AnimeSimByMediaType = animeSimByMediaType,
            SampleAnimeIds = sampleAnimeIds,
            SampleRecsCount = sampleRecsCount,
        };
    }
}
