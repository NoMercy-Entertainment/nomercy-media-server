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

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NoMercy.PluginSdk.Browser;

/// <summary>Creates Chrome suspended on a private desktop, then puts it in a kill-on-close job.</summary>
internal sealed class WindowsChromeDesktop : IDisposable
{
    private const uint DesktopAllAccess = 0x01ff;
    private const uint CreateSuspended = 0x00000004;
    private const uint KillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformation = 9;

    private readonly IntPtr _desktop;
    private readonly string _name;
    private IntPtr _job;

    private WindowsChromeDesktop(IntPtr desktop, string name)
    {
        _desktop = desktop;
        _name = name;
    }

    internal static WindowsChromeDesktop Create()
    {
        string name = "NoMercyPluginBrowser_" + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktop(name, null, IntPtr.Zero, 0, DesktopAllAccess, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not create a hidden desktop."
            );
        return new WindowsChromeDesktop(desktop, name);
    }

    internal Process Start(string executable, IReadOnlyList<string> arguments)
    {
        StartupInfo startup = new() { Size = Marshal.SizeOf<StartupInfo>(), Desktop = _name };
        StringBuilder command = new('"' + executable + '"');
        foreach (string argument in arguments)
            command
                .Append(' ')
                .Append('"')
                .Append(argument.Replace("\"", "\\\"", StringComparison.Ordinal))
                .Append('"');

        if (
            !CreateProcess(
                executable,
                command,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateSuspended,
                IntPtr.Zero,
                null,
                ref startup,
                out ProcessInformation child
            )
        )
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not start Chrome on its hidden desktop."
            );

        try
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero)
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create Chrome's job object."
                );

            ExtendedLimits limits = new()
            {
                Basic = new BasicLimits { LimitFlags = KillOnJobClose },
            };
            if (
                !SetInformationJobObject(
                    _job,
                    ExtendedLimitInformation,
                    ref limits,
                    Marshal.SizeOf<ExtendedLimits>()
                )
            )
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not bind Chrome's lifetime to the server."
                );
            if (!AssignProcessToJobObject(_job, child.Process))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not place Chrome in its job."
                );
            if (ResumeThread(child.Thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume Chrome.");

            return Process.GetProcessById(child.ProcessId);
        }
        catch
        {
            TerminateProcess(child.Process, 1);
            throw;
        }
        finally
        {
            CloseHandle(child.Thread);
            CloseHandle(child.Process);
        }
    }

    public void Dispose()
    {
        if (_job != IntPtr.Zero)
        {
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
        CloseDesktop(_desktop);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
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
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryUsed;
        public IntPtr PeakJobMemoryUsed;
    }

    [DllImport(
        "user32.dll",
        EntryPoint = "CreateDesktopW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern IntPtr CreateDesktop(
        string name,
        string? device,
        IntPtr devmode,
        uint flags,
        uint access,
        IntPtr security
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateProcessW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string application,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startup,
        out ProcessInformation process
    );

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateJobObjectW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        ref ExtendedLimits information,
        int length
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
