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
using NoMercy.Queue.MediaServer.Jobs;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Api.Services;

public sealed class StorageSettingsService(
    DatabaseMaintenanceSettings databaseSettings,
    ImageCacheSettings imageSettings,
    BackupSettings backupSettings,
    IConfigurationStore store
) : IStorageSettingsService
{
    private const long BytesPerMegabyte = 1024 * 1024;

    private sealed record Field(
        string Name,
        string Key,
        int? Value,
        int Min,
        int Max,
        bool NeedsRestart
    );

    public StorageSettings Read() =>
        new(
            databaseSettings.Hour,
            (int)databaseSettings.FailedJobRetention.TotalDays,
            backupSettings.RetainCount,
            (int)(imageSettings.MaxBytes / BytesPerMegabyte),
            (int)imageSettings.OrphanGrace.TotalDays,
            imageSettings.Hour,
            backupSettings.PruneHandMadeCopies
        );

    public async Task<StorageSettingsChange> ApplyAsync(StorageSettingsPatch patch, Guid userId)
    {
        List<Field> given =
        [
            .. new Field[]
            {
                new(
                    "database_hour",
                    DatabaseMaintenanceSettings.HourKey,
                    patch.DatabaseHour,
                    0,
                    23,
                    true
                ),
                new(
                    "failed_job_retention_days",
                    DatabaseMaintenanceSettings.FailedJobRetentionDaysKey,
                    patch.FailedJobRetentionDays,
                    1,
                    int.MaxValue,
                    false
                ),
                new(
                    "backup_retain_count",
                    BackupSettings.RetainCountKey,
                    patch.BackupRetainCount,
                    1,
                    int.MaxValue,
                    true
                ),
                new(
                    "image_cache_max_megabytes",
                    ImageCacheSettings.MaxMegabytesKey,
                    patch.ImageCacheMaxMegabytes,
                    0,
                    int.MaxValue,
                    false
                ),
                new(
                    "image_cache_orphan_grace_days",
                    ImageCacheSettings.OrphanGraceDaysKey,
                    patch.ImageCacheOrphanGraceDays,
                    1,
                    int.MaxValue,
                    false
                ),
                new(
                    "image_cache_hour",
                    ImageCacheSettings.HourKey,
                    patch.ImageCacheHour,
                    0,
                    23,
                    true
                ),
            }.Where(field => field.Value is not null),
        ];

        Field? bad = given.FirstOrDefault(field =>
            field.Value < field.Min || field.Value > field.Max
        );
        if (bad is not null)
            return new(
                null,
                bad.Max == int.MaxValue
                    ? $"{bad.Name} must be {bad.Min} or more."
                    : $"{bad.Name} must be {bad.Min} to {bad.Max}.",
                false
            );

        foreach (Field field in given)
            await store.SetValueAsync(field.Key, field.Value!.Value.ToString(), userId);

        if (patch.BackupPruneHandMadeCopies is { } pruneHandMade)
            await store.SetValueAsync(
                BackupSettings.PruneHandMadeCopiesKey,
                pruneHandMade ? "true" : "false",
                userId
            );

        return new(Read(), null, given.Any(field => field.NeedsRestart));
    }
}
