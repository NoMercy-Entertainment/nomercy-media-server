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
using System.Globalization;
using System.Runtime.Versioning;
using NoMercy.PluginSdk.Quotas;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// A plugin process in its own cgroup v2 slice.
/// <para>
/// The kernel is what refuses the allocation, at the moment it is made. A
/// quota the supervisor polls is one a plugin can exceed between two polls,
/// and by the time anybody notices the machine has already swapped.
/// </para>
/// <para>
/// A child the plugin starts lands in its parent's cgroup with nothing to
/// configure, so <c>process.spawn</c> is confined by the same three numbers
/// as the plugin itself. Closing the slice kills everything still in it, so a
/// plugin cannot outlive the server that started it.
/// </para>
/// <para>
/// Namespaces and a seccomp filter are not here. They need a package the
/// owner may not have, and a sandbox that silently did less than it claimed
/// is worse than one that says what it does.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxCgroupSandbox : IPluginSandbox, IDisposable
{
    private const string CgroupRoot = "/sys/fs/cgroup";

    private readonly string? _slice;

    public LinuxCgroupSandbox()
    {
        _slice = CreateSlice();
    }

    public bool Available => _slice is not null;

    public bool Confine(Process process, PluginQuota quota)
    {
        if (_slice is null)
            return false;

        foreach ((string file, string value) in PluginCgroupLimits.For(quota))
            if (!Write(file, value))
                return false;

        // Last: a pid written before the limits are in place is a process that
        // ran unconfined for however long the writes took.
        return Write("cgroup.procs", process.Id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The memory ceiling the kernel holds, so a test can read it back.</summary>
    public long? MemoryLimitInKernel() => Number(Read("memory.max"));

    /// <summary>How many processes the kernel will let this slice hold.</summary>
    public long? ProcessLimitInKernel() => Number(Read("pids.max"));

    /// <summary>The CPU quota in microseconds per hundred-millisecond period.</summary>
    public long? CpuQuotaInKernel()
    {
        string? line = Read("cpu.max");

        return line is null ? null : Number(line.Split(' ')[0]);
    }

    private static string? CreateSlice()
    {
        try
        {
            if (!Directory.Exists(CgroupRoot))
                return null;

            string parent = Path.Combine(CgroupRoot, "nomercy.plugins");
            Directory.CreateDirectory(parent);

            // Without this the slice below has no memory.max, no cpu.max and
            // no pids.max to write: a controller reaches a cgroup's children
            // only once its parent hands the controller down.
            File.WriteAllText(Path.Combine(parent, "cgroup.subtree_control"), "+memory +cpu +pids");

            string slice = Path.Combine(parent, Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(slice);

            return slice;
        }
        catch (Exception)
        {
            // An unprivileged server with no delegated subtree cannot make one.
            // Reporting that is the point: a stub that returned success would
            // put a plugin into what the rest of the server believes is a
            // sandbox.
            return null;
        }
    }

    private bool Write(string file, string value)
    {
        if (_slice is null)
            return false;

        try
        {
            File.WriteAllText(Path.Combine(_slice, file), value);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string? Read(string file)
    {
        if (_slice is null)
            return null;

        try
        {
            return File.ReadAllText(Path.Combine(_slice, file)).Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long? Number(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;

    /// <summary>
    /// Kills what is still in the slice and removes it. A child left running
    /// keeps its port, its files and its memory, and the next start finds all
    /// three taken by something it cannot see.
    /// </summary>
    public void Dispose()
    {
        if (_slice is null)
            return;

        Write("cgroup.kill", "1");

        try
        {
            Directory.Delete(_slice);
        }
        catch (Exception)
        {
            // A slice the kernel still considers busy is removed by the next
            // start, and failing a shutdown over it helps nobody.
        }
    }
}
