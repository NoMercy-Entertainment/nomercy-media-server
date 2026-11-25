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

using Newtonsoft.Json;
using NoMercy.Api.Services;

namespace NoMercy.Api.DTOs.Dashboard;

public record StorageSettingsDto
{
    [JsonProperty("database_hour")]
    public int DatabaseHour { get; init; }

    [JsonProperty("failed_job_retention_days")]
    public int FailedJobRetentionDays { get; init; }

    [JsonProperty("backup_retain_count")]
    public int BackupRetainCount { get; init; }

    [JsonProperty("image_cache_max_megabytes")]
    public int ImageCacheMaxMegabytes { get; init; }

    [JsonProperty("image_cache_orphan_grace_days")]
    public int ImageCacheOrphanGraceDays { get; init; }

    [JsonProperty("image_cache_hour")]
    public int ImageCacheHour { get; init; }

    public static StorageSettingsDto From(StorageSettings settings) =>
        new()
        {
            DatabaseHour = settings.DatabaseHour,
            FailedJobRetentionDays = settings.FailedJobRetentionDays,
            BackupRetainCount = settings.BackupRetainCount,
            ImageCacheMaxMegabytes = settings.ImageCacheMaxMegabytes,
            ImageCacheOrphanGraceDays = settings.ImageCacheOrphanGraceDays,
            ImageCacheHour = settings.ImageCacheHour,
        };
}
