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

using FluentAssertions;
using NoMercy.PluginSdk.OutOfProcess;
using NoMercy.PluginSdk.Quotas;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A quota as the three lines cgroup v2 reads.
/// <para>
/// These run on every machine that builds this, not only on a Linux one that
/// also grants the server a cgroup of its own. A number that reached the wrong
/// file, or the right file in the wrong unit, is a plugin with no ceiling on a
/// kernel that reported no error.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginCgroupLimitsTests
{
    [Fact]
    public void TheMemoryCeilingIsWrittenInBytesToMemoryMax()
    {
        Value(new PluginQuota(25, 256L * 1024 * 1024, 0, 0), "memory.max").Should().Be("268435456");
    }

    /// <summary>
    /// Zero is the owner turning the ceiling off. Writing it would be a
    /// process allowed no memory at all, which the kernel answers by killing
    /// the plugin as soon as it starts.
    /// </summary>
    [Fact]
    public void NoCeilingIsWrittenAsMaxRatherThanAsZero()
    {
        Value(new PluginQuota(25, 0, 0, 0), "memory.max").Should().Be("max");
    }

    /// <summary>
    /// The share is a percentage of one processor, and the kernel counts
    /// microseconds of one processor per period, so a hundredth of the period
    /// is a percent.
    /// </summary>
    [Fact]
    public void TheCpuShareIsWrittenAsMicrosecondsOfOneProcessorPerPeriod()
    {
        Value(new PluginQuota(25, 1, 0, 0), "cpu.max").Should().Be("25000 100000");
    }

    [Fact]
    public void ACpuShareTheOwnerTurnedOffIsWrittenAsMax()
    {
        Value(new PluginQuota(0, 1, 0, 0), "cpu.max").Should().Be("max");
    }

    /// <summary>
    /// A plugin's child runs inside the plugin's own slice, so the cap has to
    /// allow more than the one process the server put in it, and still refuse
    /// a plugin that spawns in a loop.
    /// </summary>
    [Fact]
    public void TheProcessCapIsTheSameNumberEveryPlatformUses()
    {
        Value(new PluginQuota(25, 1, 0, 0), "pids.max")
            .Should()
            .Be(PluginSandboxLimits.ProcessesPerPlugin.ToString());
    }

    /// <summary>
    /// Every one of the three is written. A limit left out is a ceiling the
    /// owner was shown and the kernel never heard of.
    /// </summary>
    [Fact]
    public void AllThreeLimitsAreWrittenAndNothingElseIs()
    {
        PluginCgroupLimits
            .For(new PluginQuota(25, 1, 0, 0))
            .Select(limit => limit.File)
            .Should()
            .BeEquivalentTo(["memory.max", "cpu.max", "pids.max"]);
    }

    private static string Value(PluginQuota quota, string file) =>
        PluginCgroupLimits.For(quota).Single(limit => limit.File == file).Value;
}
