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
using NoMercy.PluginSdk.OutOfProcess;
using NoMercy.PluginSdk.Quotas;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The Windows sandbox, asserted against the Windows kernel.
/// <para>
/// A sandbox whose test never ran on the kernel that enforces it proves
/// nothing, so these read the limits back out of the operating system and
/// watch a real process die rather than asserting on a value this code just
/// wrote to itself.
/// </para>
/// <para>
/// Skipped rather than failed off Windows: the class is correct there and
/// absent elsewhere, and a red test on Linux would say the code is broken
/// when it is simply not the platform's.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class WindowsJobObjectSandboxTests
{
    private static bool OnWindows => OperatingSystem.IsWindows();

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void TheMemoryCeiling_ReachesTheKernelRatherThanStayingInOurOwnObject()
    {
        Skip.IfNot(OnWindows, "Job objects are a Windows facility.");

        using WindowsJobObjectSandbox sandbox = new();
        using Process process = Sleeper();

        sandbox.Confine(process, Quota(memoryBytes: 256L * 1024 * 1024)).Should().BeTrue();

        // Read back from Windows, not from a field this test set.
        sandbox.MemoryLimitInKernel().Should().Be(256L * 1024 * 1024);

        process.Kill(entireProcessTree: true);
    }

    /// <summary>
    /// A child left running after the server dies keeps its port, its files
    /// and its memory, and the next start finds all three taken by something
    /// it cannot see.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void ClosingTheJob_KillsThePluginProcessWithIt()
    {
        Skip.IfNot(OnWindows, "Job objects are a Windows facility.");

        Process process = Sleeper();

        using (WindowsJobObjectSandbox sandbox = new())
        {
            sandbox.Confine(process, Quota(memoryBytes: 256L * 1024 * 1024)).Should().BeTrue();
            process.HasExited.Should().BeFalse();
        }

        process
            .WaitForExit(milliseconds: 10_000)
            .Should()
            .BeTrue("closing the job kills its processes");
        process.Dispose();
    }

    /// <summary>
    /// Zero is the owner turning the ceiling off. Handing it to the kernel
    /// would be a process allowed no memory at all — a plugin that cannot
    /// start rather than one nobody limited.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void NoCeiling_LeavesTheKernelWithoutOneRatherThanOneOfZero()
    {
        Skip.IfNot(OnWindows, "Job objects are a Windows facility.");

        using WindowsJobObjectSandbox sandbox = new();
        using Process process = Sleeper();

        sandbox.Confine(process, Quota(memoryBytes: 0)).Should().BeTrue();
        sandbox.MemoryLimitInKernel().Should().BeNull();

        process.Kill(entireProcessTree: true);
    }

    /// <summary>
    /// The kill is not part of the memory ceiling. A plugin whose owner turned
    /// the ceiling off is still one whose children must die with the server,
    /// and a child left running keeps its port, its files and its memory until
    /// the machine is rebooted.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void ClosingTheJob_KillsThePluginProcessEvenWithNoMemoryCeiling()
    {
        Skip.IfNot(OnWindows, "Job objects are a Windows facility.");

        Process process = Sleeper();

        using (WindowsJobObjectSandbox sandbox = new())
            sandbox.Confine(process, Quota(memoryBytes: 0)).Should().BeTrue();

        process
            .WaitForExit(milliseconds: 10_000)
            .Should()
            .BeTrue("closing the job kills its processes");
        process.Dispose();
    }

    /// <summary>
    /// A plugin's child process runs inside the plugin's own sandbox, so the
    /// job has to allow more than the one process the server put in it.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void TheJobAllowsThePluginToSpawnChildrenOfItsOwn()
    {
        Skip.IfNot(OnWindows, "Job objects are a Windows facility.");

        using WindowsJobObjectSandbox sandbox = new();
        using Process process = Sleeper();

        sandbox.Confine(process, Quota(memoryBytes: 256L * 1024 * 1024)).Should().BeTrue();

        sandbox.ActiveProcessLimitInKernel().Should().BeGreaterThan(1);

        process.Kill(entireProcessTree: true);
    }

    /// <summary>
    /// The share is a percentage of one processor and Windows counts the cap
    /// in hundredths of a percent of the WHOLE machine, so a quarter of one
    /// processor on an eight-core box is 25 percent here and 3.125 percent of
    /// the machine there.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void TheCpuShare_IsAShareOfOneProcessorAndNotOfTheWholeMachine()
    {
        Skip.IfNot(OnWindows, "Job objects are a Windows facility.");

        using WindowsJobObjectSandbox sandbox = new();
        using Process process = Sleeper();

        sandbox.Confine(process, new PluginQuota(25, 256L * 1024 * 1024, 0, 0)).Should().BeTrue();

        sandbox
            .CpuRateInKernel()
            .Should()
            .Be((uint)(25 * 100 / Math.Max(1, Environment.ProcessorCount)));

        process.Kill(entireProcessTree: true);
    }

    private static PluginQuota Quota(long memoryBytes) => new(25, memoryBytes, 0, 0);

    /// <summary>A process that does nothing and waits to be told to stop.</summary>
    private static Process Sleeper()
    {
        Process process = Process.Start(
            new ProcessStartInfo("cmd.exe", "/c pause")
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        )!;

        return process;
    }
}
