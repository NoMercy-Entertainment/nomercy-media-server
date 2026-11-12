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

namespace NoMercy.NmSystem.SystemCalls;

/// <summary>
/// The decisions behind a server restart: who brings the server back, how a
/// self-relaunch is started, and how the new process waits for the old one.
/// </summary>
public static class ServerRestartPlan
{
    /// <summary>Exit code that asks a supervisor to start the server again.</summary>
    public const int RestartExitCode = 75;

    public const string SupervisorVariable = "NOMERCY_SUPERVISOR";
    public const string LauncherSupervisor = "launcher";
    public const string WaitPidVariable = "NOMERCY_RESTART_WAIT_PID";

    private const string SystemdInvocationVariable = "INVOCATION_ID";
    private const string ContainerVariable = "DOTNET_RUNNING_IN_CONTAINER";
    private const string LaunchdJobVariable = "XPC_SERVICE_NAME";

    public static readonly TimeSpan WaitCap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// True when systemd, launchd, a container runtime or the Launcher restarts this process
    /// after it exits with <see cref="RestartExitCode"/>.
    /// </summary>
    public static bool IsSupervised(Func<string, string?> getEnvironmentVariable)
    {
        if (!string.IsNullOrEmpty(getEnvironmentVariable(SystemdInvocationVariable)))
            return true;

        if (getEnvironmentVariable(ContainerVariable) is "true" or "1")
            return true;

        // launchd names the job it runs; a Terminal session carries "0".
        string? launchdJob = getEnvironmentVariable(LaunchdJobVariable);
        if (!string.IsNullOrEmpty(launchdJob) && launchdJob != "0")
            return true;

        return getEnvironmentVariable(SupervisorVariable) == LauncherSupervisor;
    }

    /// <summary>
    /// Start info for a copy of this process: same arguments, working folder and
    /// environment, plus the PID the copy waits for before it binds its ports.
    /// </summary>
    public static ProcessStartInfo CreateRelaunchStartInfo(
        string processPath,
        IReadOnlyList<string> commandLineArgs,
        string workingDirectory,
        int processId
    )
    {
        ProcessStartInfo startInfo = new(processPath)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };

        // Under the dotnet host the first argument is the server assembly, not the executable.
        bool isDotnetHost = Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        foreach (string argument in commandLineArgs.Skip(isDotnetHost ? 0 : 1))
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment[WaitPidVariable] = processId.ToString(CultureInfo.InvariantCulture);

        return startInfo;
    }

    /// <summary>
    /// Waits for the server that relaunched this process to exit, so its ports are
    /// free. Always clears the variable, so a later restart inherits no stale PID.
    /// </summary>
    /// <returns>False when the previous server is still running after the cap.</returns>
    public static bool WaitForPreviousServer(
        Func<string, string?> getEnvironmentVariable,
        Action<string, string?> setEnvironmentVariable,
        Func<int, bool> isProcessRunning,
        Action<TimeSpan> pause,
        TimeSpan cap,
        TimeSpan pollInterval
    )
    {
        string? value = getEnvironmentVariable(WaitPidVariable);
        setEnvironmentVariable(WaitPidVariable, null);

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            return true;

        TimeSpan waited = TimeSpan.Zero;
        while (isProcessRunning(pid))
        {
            if (waited >= cap)
                return false;

            pause(pollInterval);
            waited += pollInterval;
        }

        return true;
    }

    public static bool WaitForPreviousServer() =>
        WaitForPreviousServer(
            Environment.GetEnvironmentVariable,
            Environment.SetEnvironmentVariable,
            IsProcessRunning,
            Thread.Sleep,
            WaitCap,
            PollInterval
        );

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
