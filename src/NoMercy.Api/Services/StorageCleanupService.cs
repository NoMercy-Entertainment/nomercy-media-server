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
using NoMercy.Database;
using NoMercy.Queue.MediaServer.Jobs;

namespace NoMercy.Api.Services;

public sealed class StorageCleanupService(
    IMaintenancePass<DatabaseMaintenanceResult> databases,
    IBackupService backups,
    IMaintenancePass<ImageCacheSweepResult?> images,
    MaintenanceGate gate
) : IStorageCleanupService
{
    public async Task<StorageCleanupResult?> RunAsync(CancellationToken cancellationToken)
    {
        using IDisposable? lease = gate.TryEnter();
        if (lease is null)
            return null;

        DatabaseMaintenanceResult database = await databases.RunAsync(cancellationToken);
        BackupPruneResult backup = await backups.PruneAsync(cancellationToken);
        ImageCacheSweepResult? image = await images.RunAsync(cancellationToken);

        return new(
            [
                new("database_media", Freed(database.Media)),
                new("database_queue", Freed(database.Queue)),
                new("backup_sets", backup.SetBytes),
                new("backup_copies", backup.CopyBytes),
                new("image_cache", image is null ? 0 : image.OrphanBytes + image.EvictedBytes),
            ],
            database.FailedJobsRemoved
        );
    }

    private static long Freed(SqliteCompactionResult? result) =>
        result is null ? 0 : result.BytesBefore - result.BytesAfter;
}
