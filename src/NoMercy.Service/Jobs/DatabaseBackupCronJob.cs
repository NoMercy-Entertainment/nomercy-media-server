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

using NoMercy.Data.ServerBackups;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Service.Jobs;

/// <summary>
/// Takes a daily backup set (every database plus the settings folder), independent
/// of the copy taken before a migration: that one only fires when a migration is
/// pending, which on a stable install can be months apart. Without this job,
/// corruption between migrations (a crash, an unclean shutdown) has no recent
/// recovery point to fall back to. The backup service keeps the newest five.
/// </summary>
public class DatabaseBackupCronJob(ILogger<DatabaseBackupCronJob> logger, IBackupService backups)
    : ICronJobExecutor
{
    public const string Reason = "daily scheduled backup";

    public string CronExpression => new CronExpressionBuilder().Daily(4);
    public string JobName => "Daily Database Backup";

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        // A failed backup must not fail the cron worker; the next day tries again.
        try
        {
            BackupSetSummary backup = await backups.CreateAsync(Reason, cancellationToken);
            logger.LogInformation(
                "Daily backup complete: {BackupId}, {SizeBytes} bytes",
                backup.Id,
                backup.SizeBytes
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Daily backup failed: {Reason}", exception.Message);
        }
    }
}
