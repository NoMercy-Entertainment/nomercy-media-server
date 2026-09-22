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

namespace NoMercy.PluginSdk.Quotas;

/// <summary>
/// What one plugin is allowed on this server.
/// <para>
/// The defaults are a share of the machine rather than a fixed number,
/// because the same plugin runs on a laptop and on a rack server and a number
/// that suits one insults the other.
/// </para>
/// <para>
/// Download is not metered. A plugin fetching is the owner's own bandwidth
/// being used for something they installed on purpose; a plugin sending is
/// the owner's uplink being spent on somebody else, and an uplink is the
/// scarce half of most connections.
/// </para>
/// </summary>
public sealed record PluginQuota(
    double CpuPercent,
    long MemoryBytes,
    long DiskBytes,
    long UploadBytesPerSecond
)
{
    /// <summary>A quota the owner turned off, which meters nothing.</summary>
    public static PluginQuota Unlimited { get; } =
        new(double.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue);

    /// <summary>
    /// A quarter of the processors, a tenth of the memory, five gigabytes of
    /// disk and a quarter of the uplink.
    /// <para>
    /// The memory floor is what stops the share being useless: a tenth of two
    /// gigabytes is two hundred megabytes, and a plugin that cannot hold a
    /// playlist is a plugin that does not work at all.
    /// </para>
    /// </summary>
    public static PluginQuota DefaultFor(
        int cores,
        long totalMemoryBytes,
        long uplinkBytesPerSecond
    ) =>
        new(
            Math.Max(1, cores) * 100d / 4,
            Math.Max(256L * 1024 * 1024, totalMemoryBytes / 10),
            5L * 1024 * 1024 * 1024,
            Math.Max(1, uplinkBytesPerSecond / 4)
        );
}
