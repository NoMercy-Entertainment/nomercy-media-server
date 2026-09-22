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
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NoMercy.Plugins.Quotas;

namespace NoMercy.Plugins.OutOfProcess;

/// <summary>
/// A plugin process in a Windows job object.
/// <para>
/// The memory ceiling is the reason this exists. A quota the supervisor polls
/// is one a plugin can exceed between two polls, and by the time anybody
/// notices the machine has already swapped. A job object refuses the
/// allocation itself, at the moment it is made.
/// </para>
/// <para>
/// The job is killed when its handle closes, so a plugin cannot outlive the
/// server that started it. A child left running after a crash keeps its port,
/// its files and its memory, and the next start finds all three taken by
/// something it cannot see.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsJobObjectSandbox : IPluginSandbox, IDisposable
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectCpuRateControlInformation = 15;

    private const uint LimitActiveProcess = 0x00000008;
    private const uint LimitProcessMemory = 0x00000100;
    private const uint LimitKillOnJobClose = 0x00002000;

    /// <summary>
    /// The plugin's own process plus fifteen children.
    /// <para>
    /// A cap rather than none: a plugin that spawns in a loop is the cheapest
    /// way to take a machine down, and the count is the only thing the kernel
    /// can refuse before the machine is already unusable.
    /// </para>
    /// </summary>
    private const uint ProcessesPerPlugin = 16;

    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;

    private readonly nint _job;

    public WindowsJobObjectSandbox()
    {
        _job = CreateJobObject(nint.Zero, null);
    }

    public bool Available => OperatingSystem.IsWindows() && _job != nint.Zero;

    public bool Confine(Process process, PluginQuota quota)
    {
        if (!Available)
            return false;

        if (!ApplyBasicLimits(quota) || !ApplyCpu(quota))
            return false;

        return AssignProcessToJobObject(_job, process.Handle);
    }

    private bool ApplyBasicLimits(PluginQuota quota)
    {
        // Zero means the owner turned the ceiling off deliberately. Passing it
        // to the kernel would be a process allowed no memory at all, which is
        // a plugin that cannot start rather than one nobody limited. The kill
        // and the process cap are applied either way: a plugin with no memory
        // ceiling is still one whose children must die with the server.
        bool hasCeiling = quota.MemoryBytes > 0;

        ExtendedLimitInformation limits = new()
        {
            BasicLimitInformation = new BasicLimitInformation
            {
                LimitFlags =
                    LimitKillOnJobClose
                    | LimitActiveProcess
                    | (hasCeiling ? LimitProcessMemory : 0),
                ActiveProcessLimit = ProcessesPerPlugin,
            },
            ProcessMemoryLimit = hasCeiling ? (nuint)quota.MemoryBytes : 0,
        };

        int size = Marshal.SizeOf(limits);
        nint buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);

            return SetInformationJobObject(
                _job,
                JobObjectExtendedLimitInformation,
                buffer,
                (uint)size
            );
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool ApplyCpu(PluginQuota quota)
    {
        if (quota.CpuPercent <= 0)
            return true;

        // Windows counts the cap in hundredths of a percent of total machine
        // capacity, and refuses anything outside 1..10000.
        uint rate = (uint)Math.Clamp(quota.CpuPercent * 100, 1, 10000);

        CpuRateControlInformation control = new()
        {
            ControlFlags = CpuRateControlEnable | CpuRateControlHardCap,
            CpuRate = rate,
        };

        int size = Marshal.SizeOf(control);
        nint buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(control, buffer, fDeleteOld: false);

            return SetInformationJobObject(
                _job,
                JobObjectCpuRateControlInformation,
                buffer,
                (uint)size
            );
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads back what the kernel actually holds, so a test can see it.</summary>
    public long? MemoryLimitInKernel()
    {
        ExtendedLimitInformation? limits = Limits();

        if (limits is null)
            return null;

        return (limits.Value.BasicLimitInformation.LimitFlags & LimitProcessMemory) == 0
            ? null
            : (long)limits.Value.ProcessMemoryLimit;
    }

    /// <summary>How many processes the kernel will let this job hold.</summary>
    public uint? ActiveProcessLimitInKernel()
    {
        ExtendedLimitInformation? limits = Limits();

        if (limits is null)
            return null;

        return (limits.Value.BasicLimitInformation.LimitFlags & LimitActiveProcess) == 0
            ? null
            : limits.Value.BasicLimitInformation.ActiveProcessLimit;
    }

    private ExtendedLimitInformation? Limits()
    {
        if (!Available)
            return null;

        int size = Marshal.SizeOf<ExtendedLimitInformation>();
        nint buffer = Marshal.AllocHGlobal(size);

        try
        {
            return QueryInformationJobObject(
                _job,
                JobObjectExtendedLimitInformation,
                buffer,
                (uint)size,
                out _
            )
                ? Marshal.PtrToStructure<ExtendedLimitInformation>(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_job != nint.Zero)
            CloseHandle(_job);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControlInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateJobObjectW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint job,
        uint infoClass,
        nint info,
        uint length
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        nint job,
        uint infoClass,
        nint info,
        uint length,
        out uint returned
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
