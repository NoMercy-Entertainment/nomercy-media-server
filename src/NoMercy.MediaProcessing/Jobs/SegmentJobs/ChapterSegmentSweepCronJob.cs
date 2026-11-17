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

using Microsoft.Extensions.Logging;
using NoMercy.MediaProcessing.Segments;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Jobs.SegmentJobs;

/// <summary>
/// The chapter segment backfill. Once a day it classifies every episode and
/// movie whose stored chapters.vtt exists, so files encoded before the
/// classifier, and chapters renamed and rescanned since, get their segments.
/// It reads only the database (no media file, no ffmpeg), so unlike the
/// fingerprint jobs it has no playback to yield to. A run with nothing new
/// writes nothing.
/// </summary>
public class ChapterSegmentSweepCronJob(
    IChapterSegmentWriter writer,
    ILogger<ChapterSegmentSweepCronJob> logger
) : ICronJobExecutor
{
    // 05:30 UTC, after the 04:00 database backup.
    private const int RunHourUtc = 5;
    private const int RunMinute = 30;

    public string CronExpression => new CronExpressionBuilder().Daily(RunHourUtc, RunMinute);

    public string JobName => "Chapter Segment Sweep";

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        int changed = await writer.ClassifyAllAsync(cancellationToken);

        logger.LogInformation(
            "Chapter segment sweep: segments changed for {Count} episode(s) or movie(s)",
            changed
        );
    }
}
