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
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Queue.MediaServer.Jobs;

public class DatabaseMaintenanceCronJob(
    IDbContextFactory<MediaContext> mediaContextFactory,
    IDbContextFactory<QueueContext> queueContextFactory,
    DatabaseMaintenanceSettings settings,
    SqliteCompactor compactor,
    ILogger<DatabaseMaintenanceCronJob> logger
) : ICronJobExecutor
{
    public string CronExpression => new CronExpressionBuilder().Daily(settings.Hour, 30);
    public string JobName => "Database Maintenance";

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        await PruneFailedJobsAsync(cancellationToken);

        await CompactAsync("media.db", mediaContextFactory, cancellationToken);
        await CompactAsync("queue.db", queueContextFactory, cancellationToken);
    }

    private async Task PruneFailedJobsAsync(CancellationToken cancellationToken)
    {
        DateTime cutoff = DateTime.UtcNow - settings.FailedJobRetention;

        try
        {
            await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
                cancellationToken
            );
            int deleted = await context
                .FailedJobs.Where(f => f.FailedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);

            logger.LogInformation(
                "Failed job prune removed {Count} rows older than {Cutoff}",
                deleted,
                cutoff
            );
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Failed job prune failed: {Reason}", exception.Message);
        }
    }

    private async Task CompactAsync<TContext>(
        string name,
        IDbContextFactory<TContext> factory,
        CancellationToken cancellationToken
    )
        where TContext : DbContext
    {
        // A failed pass must not fail the cron worker; the next night tries again.
        try
        {
            await using TContext context = await factory.CreateDbContextAsync(cancellationToken);
            await context.Database.OpenConnectionAsync(cancellationToken);
            SqliteCompactionResult result = await compactor.CompactIfWorthItAsync(
                context.Database.GetDbConnection(),
                settings.MinFreeRatio,
                settings.MinFreeBytes,
                cancellationToken
            );

            logger.LogInformation(
                "Database compaction {Name}: compacted {Compacted}, {Before} bytes to {After} bytes ({Free} bytes were free)",
                name,
                result.Compacted,
                result.BytesBefore,
                result.BytesAfter,
                result.FreeBytesBefore
            );
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                exception,
                "Database compaction {Name} failed: {Reason}",
                name,
                exception.Message
            );
        }
    }
}
