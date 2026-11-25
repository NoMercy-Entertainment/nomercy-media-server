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

public class ImageCacheSettings(IConfigurationStore configurationStore)
{
    public const string HourKey = "maintenance.image_cache.hour";
    public const string MaxMegabytesKey = "maintenance.image_cache.max_megabytes";
    public const string OrphanGraceDaysKey = "maintenance.image_cache.orphan_grace_days";

    public const int DefaultHour = 4;
    public const int DefaultMaxMegabytes = 10240;
    public const int DefaultOrphanGraceDays = 14;

    private const long BytesPerMegabyte = 1024 * 1024;

    public int Hour => ReadInt(HourKey, DefaultHour, 0, 23);

    /// <summary>The cache size the nightly sweep brings the images under. 0 means no limit.</summary>
    public long MaxBytes => ReadInt(MaxMegabytesKey, DefaultMaxMegabytes, 0) * BytesPerMegabyte;

    public TimeSpan OrphanGrace =>
        TimeSpan.FromDays(ReadInt(OrphanGraceDaysKey, DefaultOrphanGraceDays, 1));

    private int ReadInt(string key, int fallback, int min, int max = int.MaxValue)
    {
        string? raw = configurationStore.GetValue(key);
        return int.TryParse(raw, out int parsed) && parsed >= min && parsed <= max
            ? parsed
            : fallback;
    }
}
