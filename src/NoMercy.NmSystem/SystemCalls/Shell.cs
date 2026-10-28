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
using System.Text;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.NmSystem.SystemCalls;

public static class Shell
{
    public class ExecOptions
    {
        public string? WorkingDirectory { get; set; }
        public bool CaptureStdErr { get; set; } = true;
        public bool CaptureStdOut { get; set; } = true;
        public bool RedirectInput { get; set; } = false;
        public bool UseShellExecute { get; set; } = false;
        public bool CreateNoWindow { get; set; } = true;
        public bool MergeStdErrToOut { get; set; } = false; // For "2>&1" behavior

        public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    }

    public class ExecResult
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;
        public bool Success => ExitCode == 0;
    }

    public static Task<ExecResult> ExecAsync(
        string executable,
        string arguments,
        ExecOptions? options = null
    ) => ExecCoreAsync(executable, psi => psi.Arguments = arguments, options);

    /// <summary>
    /// Runs <paramref name="executable"/> with each entry passed as a
    /// discrete argv token via <see cref="ProcessStartInfo.ArgumentList"/>.
    /// Prefer this overload over the raw-string overload whenever an
    /// argument may contain untrusted or user-controlled content (paths
    /// with spaces/quotes/shell metacharacters) — no shell re-parses the
    /// tokens, so nothing needs escaping.
    /// </summary>
    public static Task<ExecResult> ExecAsync(
        string executable,
        IReadOnlyList<string> arguments,
        ExecOptions? options = null
    ) =>
        ExecCoreAsync(
            executable,
            psi =>
            {
                foreach (string argument in arguments)
                    psi.ArgumentList.Add(argument);
            },
            options
        );

    private static async Task<ExecResult> ExecCoreAsync(
        string executable,
        Action<ProcessStartInfo> configureArguments,
        ExecOptions? options
    )
    {
        options ??= new();
        using Process process = new();

        process.StartInfo.FileName = executable;
        configureArguments(process.StartInfo);
        process.StartInfo.WorkingDirectory = options.WorkingDirectory.OrEmpty();

        // Unset, .NET decodes a child's output with the host code page and mangles non-ASCII names.
        if (options.CaptureStdOut)
        {
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        }

        if (options.RedirectInput)
            process.StartInfo.RedirectStandardInput = true;

        if (options.UseShellExecute)
            process.StartInfo.UseShellExecute = true;

        if (options.CreateNoWindow)
            process.StartInfo.CreateNoWindow = true;

        if (options.MergeStdErrToOut)
            process.StartInfo.RedirectStandardError = false;
        else
        {
            process.StartInfo.RedirectStandardError = options.CaptureStdErr;
            if (options.CaptureStdErr)
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        }

        foreach (KeyValuePair<string, string> envVar in options.EnvironmentVariables)
            process.StartInfo.EnvironmentVariables[envVar.Key] = envVar.Value;

        StringBuilder outputBuilder = new();
        StringBuilder errorBuilder = new();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                outputBuilder.AppendLine(e.Data);
        };

        if (options is { CaptureStdErr: true, MergeStdErrToOut: false })
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    errorBuilder.AppendLine(e.Data);
            };

        try
        {
            process.Start();

            // Attach the started process as a child of this application so it is terminated when the parent exits.
            ChildProcessManager.Attach(process);

            if (options.CaptureStdOut)
                process.BeginOutputReadLine();
            if (options is { CaptureStdErr: true, MergeStdErrToOut: false })
                process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            string stdOutput = outputBuilder.ToString().Trim();
            string stdError = errorBuilder.ToString().Trim();

            if (options.MergeStdErrToOut)
                stdOutput += await process.StandardError.ReadToEndAsync();

            return new()
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdOutput,
                StandardError = stdError,
            };
        }
        catch (Exception ex)
        {
            return new()
            {
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = $"Error executing command: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Escapes a single value for safe interpolation into a POSIX shell
    /// command string (e.g. an <see cref="ExecCommand"/> argument that pipes
    /// through <c>awk</c>/<c>grep</c> and therefore genuinely needs a
    /// shell). Wraps the value in single quotes and escapes any embedded
    /// single quote using the standard <c>'\''</c> close-escape-reopen
    /// sequence, so no shell metacharacter in the value is ever interpreted.
    /// Prefer the argv-based <see cref="ExecAsync(string, IReadOnlyList{string}, ExecOptions?)"/>
    /// overload instead whenever a shell isn't actually required.
    /// </summary>
    public static string EscapeShellArgument(string value)
    {
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    public static string ExecCommand(string command)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(psi);
            if (process != null)
            {
                // Attach so the process is killed when the parent exits.
                ChildProcessManager.Attach(process);

                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return string.IsNullOrEmpty(output) ? "Unknown" : output;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error running command: {ex.Message}");
        }

        return "Unknown";
    }

    public static ExecResult ExecSync(
        string executable,
        string arguments,
        ExecOptions? options = null
    )
    {
        return ExecAsync(executable, arguments, options).GetAwaiter().GetResult();
    }

    public static ExecResult ExecSync(
        string executable,
        IReadOnlyList<string> arguments,
        ExecOptions? options = null
    )
    {
        return ExecAsync(executable, arguments, options).GetAwaiter().GetResult();
    }

    public static async Task<string> ExecStdOutAsync(
        string executable,
        string arguments,
        ExecOptions? options = null
    )
    {
        return (await ExecAsync(executable, arguments, options)).StandardOutput;
    }

    public static string ExecStdOutSync(
        string executable,
        string arguments,
        ExecOptions? options = null
    )
    {
        return ExecSync(executable, arguments, options).StandardOutput;
    }

    public static async Task<string> ExecStdErrAsync(
        string executable,
        string arguments,
        ExecOptions? options = null
    )
    {
        options ??= new() { CaptureStdErr = true, CaptureStdOut = false };
        return (await ExecAsync(executable, arguments, options)).StandardError;
    }

    public static string ExecStdErrSync(
        string executable,
        string arguments,
        ExecOptions? options = null
    )
    {
        options ??= new() { CaptureStdErr = true, CaptureStdOut = false };
        return ExecSync(executable, arguments, options).StandardError;
    }

    // Child process manager: attaches started processes so they are terminated when the parent exits.
    public static class ChildProcessManager
    {
        private static readonly object _lock = new();
        private static IntPtr _jobHandle = IntPtr.Zero;

        public static void Attach(Process process)
        {
            if (process == null)
                return;

            // Ensure process has started and has a handle
            if (process.HasExited)
                return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    EnsureJobObject();
                    // Assign the process to the job object
                    bool assigned = AssignProcessToJobObject(_jobHandle, process.Handle);
                    // If assignment fails, there's not much we can do - fallback to ProcessExit handler
                    if (!assigned)
                    {
                        RegisterFallback(process);
                    }
                }
                catch
                {
                    RegisterFallback(process);
                }
            }
            else
            {
                RegisterFallback(process);
            }
        }

        private static void RegisterFallback(Process process)
        {
            // Best-effort fallback for non-Windows or if job assignment fails.
            void OnExit(object? s, EventArgs e)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                { /* swallow exceptions */
                }
            }

            AppDomain.CurrentDomain.ProcessExit += OnExit;
            process.EnableRaisingEvents = true;
            process.Exited += (_, __) => AppDomain.CurrentDomain.ProcessExit -= OnExit;
        }

        private static void EnsureJobObject()
        {
            if (_jobHandle != IntPtr.Zero)
                return;

            lock (_lock)
            {
                if (_jobHandle != IntPtr.Zero)
                    return;

                _jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (_jobHandle == IntPtr.Zero)
                    throw new InvalidOperationException("CreateJobObject failed.");

                JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new()
                {
                    BasicLimitInformation = new()
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };

                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr p = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, p, false);
                    if (
                        !SetInformationJobObject(
                            _jobHandle,
                            JobObjectExtendedLimitInformation,
                            p,
                            (uint)length
                        )
                    )
                    {
                        CloseHandle(_jobHandle);
                        _jobHandle = IntPtr.Zero;
                        throw new InvalidOperationException("SetInformationJobObject failed.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(p);
                }

                // Keep the job handle open for the lifetime of the process so that when this process exits,
                // the OS will close the handle and terminate any processes associated with the job.
            }
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob,
            int JobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    public static class ProcessHelper
    {
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool FreeConsole();

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(
            CtrlTypes dwCtrlEvent,
            uint dwProcessGroupId
        );

        private enum CtrlTypes : uint
        {
            CTRL_C_EVENT = 0,
        }

        public static void SendCtrlC(Process process)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "SendCtrlC is only supported on Windows platforms."
                );

            if (AttachConsole((uint)process.Id))
            {
                GenerateConsoleCtrlEvent(CtrlTypes.CTRL_C_EVENT, 0);
                FreeConsole();
            }
        }

        /// <summary>
        /// Bind an externally-started process to the server's kill-on-close job
        /// object so it dies with this server even on a hard kill or crash —
        /// the graceful-shutdown cancellation path does not run then. Callers
        /// that launch their own process (e.g. the encoder's ProcessRunner)
        /// should call this right after Process.Start. Safe off Windows (falls
        /// back to a ProcessExit tree-kill) and if the process already exited.
        /// </summary>
        public static void AttachToParentLifetime(Process process) =>
            ChildProcessManager.Attach(process);
    }
}
