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
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Service.Jobs;

/// <summary>
/// Queues audio analysis for tracks in libraries that asked for it.
/// <para>
/// A sweep rather than a hook on import, because it covers the library a user
/// already had at the moment they turn the setting on, and it re-covers
/// everything when the analyzer version changes. Both of those are the same
/// question — "which tracks lack a current verdict" — and one mechanism answers
/// it.
/// </para>
/// </summary>
public class AudioAnalysisSweepCronJob : ICronJobExecutor
{
    /// <summary>
    /// Queued per run. A large library must not put sixty thousand rows on the
    /// queue in one pass and bury every other job behind them; the next run
    /// picks up where this one stopped.
    /// </summary>
    private const int BatchSize = 500;

    private const string MusicLibraryType = "music";

    private readonly IJobDispatcher _dispatcher;
    private readonly IAudioAnalyzer _analyzer;
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly ILogger<AudioAnalysisSweepCronJob> _logger;

    public string CronExpression => new CronExpressionBuilder().Hourly();
    public string JobName => "Audio Analysis Sweep";

    public AudioAnalysisSweepCronJob(
        IJobDispatcher dispatcher,
        IAudioAnalyzer analyzer,
        IDbContextFactory<MediaContext> contextFactory,
        ILogger<AudioAnalysisSweepCronJob> logger
    )
    {
        _dispatcher = dispatcher;
        _analyzer = analyzer;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        await using MediaContext mediaContext = await _contextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<Ulid> libraryIds = await mediaContext
            .Libraries.AsNoTracking()
            .Where(library => library.AnalyzeAudio && library.Type == MusicLibraryType)
            .Select(library => library.Id)
            .ToListAsync(cancellationToken);

        if (libraryIds.Count == 0)
        {
            return;
        }

        int version = _analyzer.Version;

        List<Guid> trackIds = await AudioAnalysisQueries
            .TracksNeedingAnalysis(mediaContext, libraryIds, version)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (trackIds.Count == 0)
        {
            return;
        }

        foreach (Guid trackId in trackIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _dispatcher.Dispatch(new MusicAnalysisJob { TrackId = trackId });
        }

        _logger.LogInformation(
            "Audio analysis sweep queued {Queued} track(s) across {Libraries} library(ies)",
            [trackIds.Count, libraryIds.Count]
        );
    }
}
