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
/// The safety net under the import hook: every hour it re-asks which tracks
/// still lack a current verdict, across every music library that wants one.
/// <para>
/// Analysis is queued when a library scan finishes, but the sweep still earns
/// its place — it covers the library a user already had at the moment they
/// turn the setting on, and it re-covers everything when the analyzer version
/// changes. That is the same question the scan hook asks, so both ask it
/// through <see cref="IAudioAnalysisScheduler" />.
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
