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

/// <inheritdoc cref="IAudioAnalysisStatisticsRepository"/>
public class AudioAnalysisStatisticsRepository(IDbContextFactory<MediaContext> mediaContextFactory)
    : IAudioAnalysisStatisticsRepository
{
    public async Task<AudioAnalysisCounts> GetCountsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using MediaContext context = await mediaContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        int analyzed = await context.TrackAudioAnalysis.CountAsync(
            analysis => analysis.State == AudioAnalysisState.Ok,
            cancellationToken
        );

        int failed = await context.TrackAudioAnalysis.CountAsync(
            analysis => analysis.State == AudioAnalysisState.Failed,
            cancellationToken
        );

        int djAnalyzed = await context.TrackDjAnalysis.CountAsync(
            dj => dj.State == AudioAnalysisState.Ok,
            cancellationToken
        );

        int djFailed = await context.TrackDjAnalysis.CountAsync(
            dj => dj.State == AudioAnalysisState.Failed,
            cancellationToken
        );

        long stemsBytes = await context.DerivedAudio.SumAsync(
            derived => derived.Bytes,
            cancellationToken
        );

        return new(analyzed, failed, djAnalyzed, djFailed, stemsBytes);
    }
}
