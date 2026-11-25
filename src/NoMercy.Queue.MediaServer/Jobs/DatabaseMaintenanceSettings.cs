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

using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Queue.MediaServer.Jobs;

// A missing or unreadable value falls back to the default, so an older install keeps working.
public class DatabaseMaintenanceSettings(IConfigurationStore configurationStore)
{
    public const string HourKey = "maintenance.database.hour";
    public const string FailedJobRetentionDaysKey =
        "maintenance.database.failed_job_retention_days";
    public const string MinFreePercentKey = "maintenance.database.min_free_percent";
    public const string MinFreeMegabytesKey = "maintenance.database.min_free_megabytes";

    public const int DefaultHour = 3;
    public const int DefaultFailedJobRetentionDays = 30;
    public const int DefaultMinFreePercent = 10;
    public const int DefaultMinFreeMegabytes = 64;

    private const long BytesPerMegabyte = 1024 * 1024;

    public int Hour => ReadInt(HourKey, DefaultHour, 0, 23);

    public TimeSpan FailedJobRetention =>
        TimeSpan.FromDays(ReadInt(FailedJobRetentionDaysKey, DefaultFailedJobRetentionDays, 1));

    public double MinFreeRatio => ReadInt(MinFreePercentKey, DefaultMinFreePercent, 0, 100) / 100.0;

    public long MinFreeBytes =>
        ReadInt(MinFreeMegabytesKey, DefaultMinFreeMegabytes, 0) * BytesPerMegabyte;

    private int ReadInt(string key, int fallback, int min, int max = int.MaxValue)
    {
        string? raw = configurationStore.GetValue(key);
        return int.TryParse(raw, out int parsed) && parsed >= min && parsed <= max
            ? parsed
            : fallback;
    }
}
