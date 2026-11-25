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
using NoMercy.NmSystem.Information;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Queue.MediaServer.Jobs;

public class ImageCacheMaintenanceCronJob(
    ImageCacheSettings settings,
    ImageReferenceReader referenceReader,
    ImageCacheSweeper sweeper,
    ILogger<ImageCacheMaintenanceCronJob> logger
) : ICronJobExecutor
{
    public string CronExpression => new CronExpressionBuilder().Daily(settings.Hour);
    public string JobName => "Image Cache Maintenance";

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        // A failed pass must not fail the cron worker; the next night tries again.
        try
        {
            // Without the reference list every image would look unused, so a failed read ends the pass.
            HashSet<string> referenced = await referenceReader.ReadAsync(cancellationToken);

            ImageCacheSweepResult result = sweeper.Sweep(
                AppFiles.ImagesPath,
                referenced,
                settings.MaxBytes,
                settings.OrphanGrace,
                DateTimeOffset.UtcNow
            );

            logger.LogInformation(
                "Image cache sweep: removed {Orphans} unused images ({OrphanBytes} bytes), evicted {Evicted} least recently used ({EvictedBytes} bytes), {After} bytes remain",
                result.OrphansRemoved,
                result.OrphanBytes,
                result.EvictedFiles,
                result.EvictedBytes,
                result.BytesAfter
            );
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Image cache sweep failed: {Reason}", exception.Message);
        }
    }
}
