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

using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using NoMercy.Plugins.OutOfProcess;
using NoMercy.Plugins.Quotas;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The Linux sandbox, asserted against the Linux kernel.
/// <para>
/// These read the limits back out of the cgroup files and watch a real process
/// die, rather than asserting on a value this code just wrote to itself. A
/// sandbox whose test never ran on the kernel that enforces it proves nothing.
/// </para>
/// <para>
/// Skipped rather than failed off Linux, and skipped on a Linux box whose
/// server cannot make a cgroup: an unprivileged run with no delegated subtree
/// is a real deployment, and a red test there would say the code is broken
/// when the machine simply does not allow it.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class LinuxCgroupSandboxTests
{
    private static bool OnLinux => OperatingSystem.IsLinux();

    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void TheMemoryCeiling_ReachesTheKernelRatherThanStayingInOurOwnObject()
    {
        Skip.IfNot(OnLinux, "Cgroups are a Linux facility.");

        using LinuxCgroupSandbox sandbox = new();
        Skip.IfNot(sandbox.Available, "This machine grants the server no cgroup of its own.");

        using Process process = Sleeper();

        sandbox.Confine(process, Quota(memoryBytes: 256L * 1024 * 1024)).Should().BeTrue();

        sandbox.MemoryLimitInKernel().Should().Be(256L * 1024 * 1024);

        process.Kill(entireProcessTree: true);
    }

    /// <summary>
    /// Zero is the owner turning the ceiling off. Writing it would be a
    /// process allowed no memory at all, which the kernel answers by killing
    /// the plugin as soon as it starts.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void NoCeiling_LeavesTheKernelWithoutOneRatherThanOneOfZero()
    {
        Skip.IfNot(OnLinux, "Cgroups are a Linux facility.");

        using LinuxCgroupSandbox sandbox = new();
        Skip.IfNot(sandbox.Available, "This machine grants the server no cgroup of its own.");

        using Process process = Sleeper();

        sandbox.Confine(process, Quota(memoryBytes: 0)).Should().BeTrue();
        sandbox.MemoryLimitInKernel().Should().BeNull();

        process.Kill(entireProcessTree: true);
    }

    /// <summary>
    /// A plugin's child process runs inside the plugin's own sandbox, so the
    /// slice has to allow more than the one process the server put in it, and
    /// still refuse a plugin that spawns in a loop.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void TheSliceAllowsThePluginToSpawnChildrenAndStillCapsThem()
    {
        Skip.IfNot(OnLinux, "Cgroups are a Linux facility.");

        using LinuxCgroupSandbox sandbox = new();
        Skip.IfNot(sandbox.Available, "This machine grants the server no cgroup of its own.");

        using Process process = Sleeper();

        sandbox.Confine(process, Quota(memoryBytes: 256L * 1024 * 1024)).Should().BeTrue();

        sandbox.ProcessLimitInKernel().Should().Be(PluginSandboxLimits.ProcessesPerPlugin);

        process.Kill(entireProcessTree: true);
    }

    /// <summary>
    /// The share is a percentage of one processor, and the kernel counts
    /// microseconds of one processor per period. A quarter of one processor is
    /// a quarter of the period.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void TheCpuShare_ReachesTheKernelAsMicrosecondsOfOneProcessor()
    {
        Skip.IfNot(OnLinux, "Cgroups are a Linux facility.");

        using LinuxCgroupSandbox sandbox = new();
        Skip.IfNot(sandbox.Available, "This machine grants the server no cgroup of its own.");

        using Process process = Sleeper();

        sandbox.Confine(process, new PluginQuota(25, 256L * 1024 * 1024, 0, 0)).Should().BeTrue();

        sandbox.CpuQuotaInKernel().Should().Be(25_000);

        process.Kill(entireProcessTree: true);
    }

    /// <summary>
    /// A child left running after the server dies keeps its port, its files
    /// and its memory, and the next start finds all three taken by something
    /// it cannot see.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void ClosingTheSlice_KillsThePluginProcessWithIt()
    {
        Skip.IfNot(OnLinux, "Cgroups are a Linux facility.");

        Process process = Sleeper();

        using (LinuxCgroupSandbox sandbox = new())
        {
            Skip.IfNot(sandbox.Available, "This machine grants the server no cgroup of its own.");

            sandbox.Confine(process, Quota(memoryBytes: 256L * 1024 * 1024)).Should().BeTrue();
            process.HasExited.Should().BeFalse();
        }

        process
            .WaitForExit(milliseconds: 10_000)
            .Should()
            .BeTrue("closing the slice kills its processes");
        process.Dispose();
    }

    private static PluginQuota Quota(long memoryBytes) => new(25, memoryBytes, 0, 0);

    /// <summary>A process that does nothing and waits to be told to stop.</summary>
    private static Process Sleeper() =>
        Process.Start(
            new ProcessStartInfo("/bin/sh", "-c \"read line\"")
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
            }
        )!;
}
