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
    MaintenanceGate gate,
    ILogger<DatabaseMaintenanceCronJob> logger
) : ICronJobExecutor, IMaintenancePass<DatabaseMaintenanceResult>
{
    public string CronExpression => new CronExpressionBuilder().Daily(settings.Hour, 30);
    public string JobName => "Database Maintenance";

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        using IDisposable lease = await gate.EnterAsync(cancellationToken);
        await RunAsync(cancellationToken);
    }

    public async Task<DatabaseMaintenanceResult> RunAsync(CancellationToken cancellationToken)
    {
        int removed = await PruneFailedJobsAsync(cancellationToken);

        SqliteCompactionResult? media = await CompactAsync(
            "media.db",
            mediaContextFactory,
            cancellationToken
        );
        SqliteCompactionResult? queue = await CompactAsync(
            "queue.db",
            queueContextFactory,
            cancellationToken
        );
        return new(removed, media, queue);
    }

    private async Task<int> PruneFailedJobsAsync(CancellationToken cancellationToken)
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
            return deleted;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Failed job prune failed: {Reason}", exception.Message);
            return 0;
        }
    }

    private async Task<SqliteCompactionResult?> CompactAsync<TContext>(
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
                settings.CheckpointWait,
                cancellationToken
            );

            logger.LogInformation(
                "Database compaction {Name}: compacted {Compacted}, {Before} bytes to {After} bytes on disk ({Free} bytes were free)",
                name,
                result.Compacted,
                result.BytesBefore,
                result.BytesAfter,
                result.FreeBytesBefore
            );
            if (result.CheckpointBlocked)
                logger.LogWarning(
                    "Database compaction {Name}: a running read kept the file from shrinking for {Wait}; the space returns once it ends",
                    name,
                    settings.CheckpointWait
                );
            return result;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                exception,
                "Database compaction {Name} failed: {Reason}",
                name,
                exception.Message
            );
            return null;
        }
    }
}
