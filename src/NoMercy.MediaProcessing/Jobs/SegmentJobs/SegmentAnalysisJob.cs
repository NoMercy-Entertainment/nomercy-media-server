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

using Microsoft.Extensions.DependencyInjection;
using NoMercy.MediaProcessing.Segments;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Jobs.SegmentJobs;

/// <summary>
/// Finds the intro, the credits and the real end of content (#598) for one
/// season as a group, or for one movie (credits and end of content only).
/// Queued after an encode and by the library-scan backfill. Runs on the
/// encoder queue, which holds off while anything is playing, at the lowest
/// priority there so encodes and subtitle OCR always go first. Idempotent:
/// a rerun replaces the earlier analysis rows.
/// </summary>
[Serializable]
public class SegmentAnalysisJob : IShouldQueue, IJobStorageInjector
{
    public string QueueName => "encoder";
    public int Priority => 1;

    public int? SeasonId { get; set; }
    public int? MovieId { get; set; }

    private ISegmentAnalysisService? _analysis;
    private ISegmentAnalysisState? _state;

    public SegmentAnalysisJob() { }

    public static SegmentAnalysisJob ForSeason(int seasonId) => new() { SeasonId = seasonId };

    public static SegmentAnalysisJob ForMovie(int movieId) => new() { MovieId = movieId };

    public void InjectStorageServices(IServiceProvider serviceProvider)
    {
        _analysis = serviceProvider.GetService<ISegmentAnalysisService>();
        _state = serviceProvider.GetService<ISegmentAnalysisState>();
    }

    public async Task Handle()
    {
        if (_analysis is null)
            return;

        if (SeasonId is { } seasonId)
        {
            await _analysis.AnalyseSeasonAsync(seasonId, CancellationToken.None);
            if (_state is not null)
                await _state.MarkSeasonAsync(seasonId, CancellationToken.None);
        }
        else if (MovieId is { } movieId)
        {
            await _analysis.AnalyseMovieAsync(movieId, CancellationToken.None);
            if (_state is not null)
                await _state.MarkMovieAsync(movieId, CancellationToken.None);
        }
    }
}
