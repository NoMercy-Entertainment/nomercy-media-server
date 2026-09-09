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
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Service.Jobs;

/// <summary>
/// The last of the three layers that keep a music library analysed, and the
/// only one that runs unprompted: every hour it re-asks which tracks still lack
/// a current verdict, across every music library that wants one.
/// <para>
/// The import queues each track it stores
/// (<see cref="NoMercy.MediaProcessing.AudioAnalysis.AudioAnalysisDispatch" />)
/// and <see cref="Subscribers.AudioAnalysisSubscriber" /> queues the tracks that
/// were already there when a scan finished. The sweep is what catches whatever
/// those two missed — a queue that was drained before a worker got to a job, a
/// library whose owner turned the setting on without rescanning, an analyzer
/// version bump. It is the same question all three ask, through
/// <see cref="IAudioAnalysisScheduler" />.
/// </para>
/// </summary>
public class AudioAnalysisSweepCronJob : ICronJobExecutor
{
    private readonly IAudioAnalysisScheduler _scheduler;
    private readonly IDbContextFactory<MediaContext> _contextFactory;

    public string CronExpression => new CronExpressionBuilder().Hourly();
    public string JobName => "Audio Analysis Sweep";

    public AudioAnalysisSweepCronJob(
        IAudioAnalysisScheduler scheduler,
        IDbContextFactory<MediaContext> contextFactory
    )
    {
        _scheduler = scheduler;
        _contextFactory = contextFactory;
    }

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        await using MediaContext mediaContext = await _contextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<Ulid> libraryIds = await AudioAnalysisQueries
            .LibrariesToAnalyze(mediaContext)
            .ToListAsync(cancellationToken);

        if (libraryIds.Count == 0)
        {
            return;
        }

        await _scheduler.QueueAsync(libraryIds, cancellationToken);
    }
}
