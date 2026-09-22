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

using System.Globalization;
using NoMercy.PluginSdk.Quotas;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// A quota as the three lines cgroup v2 reads.
/// <para>
/// Separate from the sandbox that writes them so the mapping is checked on
/// every machine that builds this, not only on a Linux one that also happens
/// to grant the server a cgroup of its own. A number that reached the wrong
/// file, or reached the right file in the wrong unit, is a plugin with no
/// ceiling on a kernel that reported no error.
/// </para>
/// </summary>
public static class PluginCgroupLimits
{
    /// <summary>The kernel counts CPU in microseconds of one processor per period.</summary>
    public const long CpuPeriodMicroseconds = 100_000;

    /// <summary>What the kernel is told to hold, file by file.</summary>
    public static IReadOnlyList<(string File, string Value)> For(PluginQuota quota) =>
        [
            ("memory.max", Memory(quota)),
            ("cpu.max", Cpu(quota)),
            ("pids.max", Text(PluginSandboxLimits.ProcessesPerPlugin)),
        ];

    /// <summary>
    /// Zero is the owner turning the ceiling off. Writing it would be a
    /// process allowed no memory at all, which the kernel answers by killing
    /// the plugin as soon as it starts.
    /// </summary>
    private static string Memory(PluginQuota quota) =>
        quota.MemoryBytes > 0 ? Text(quota.MemoryBytes) : "max";

    /// <summary>
    /// The share is a percentage of one processor, and the kernel counts
    /// microseconds of one processor per period, so a hundredth of the period
    /// is a percent.
    /// </summary>
    private static string Cpu(PluginQuota quota)
    {
        if (quota.CpuPercent <= 0)
            return "max";

        long microseconds = (long)Math.Round(quota.CpuPercent * CpuPeriodMicroseconds / 100);

        return $"{Text(Math.Max(1, microseconds))} {Text(CpuPeriodMicroseconds)}";
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
