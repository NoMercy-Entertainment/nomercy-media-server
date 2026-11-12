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
using Microsoft.Extensions.Hosting;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Api.Services;

public sealed class ServerRestarter : IServerRestarter
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<ProcessStartInfo> _createRelaunchStartInfo;
    private readonly Func<ProcessStartInfo, bool> _spawn;
    private readonly Action<int> _setExitCode;

    public ServerRestarter(IHostApplicationLifetime lifetime)
        : this(
            lifetime,
            Environment.GetEnvironmentVariable,
            CreateRelaunchStartInfoForThisProcess,
            SpawnProcess,
            exitCode => Environment.ExitCode = exitCode
        ) { }

    internal ServerRestarter(
        IHostApplicationLifetime lifetime,
        Func<string, string?> getEnvironmentVariable,
        Func<ProcessStartInfo> createRelaunchStartInfo,
        Func<ProcessStartInfo, bool> spawn,
        Action<int> setExitCode
    )
    {
        _lifetime = lifetime;
        _getEnvironmentVariable = getEnvironmentVariable;
        _createRelaunchStartInfo = createRelaunchStartInfo;
        _spawn = spawn;
        _setExitCode = setExitCode;
    }

    public ServerRestartOutcome Begin()
    {
        if (ServerRestartPlan.IsSupervised(_getEnvironmentVariable))
            return new(ServerRestartOutcome.Supervisor, null);

        try
        {
            if (_spawn(_createRelaunchStartInfo()))
                return new(ServerRestartOutcome.Relaunch, null);

            return new(ServerRestartOutcome.Relaunch, "The new server process did not start.");
        }
        catch (Exception exception)
        {
            return new(ServerRestartOutcome.Relaunch, exception.Message);
        }
    }

    public void Complete(ServerRestartOutcome outcome)
    {
        if (outcome.Error is not null)
            return;

        if (outcome.Mode == ServerRestartOutcome.Supervisor)
            _setExitCode(ServerRestartPlan.RestartExitCode);

        _lifetime.StopApplication();
    }

    private static ProcessStartInfo CreateRelaunchStartInfoForThisProcess()
    {
        string processPath =
            Environment.ProcessPath
            ?? throw new InvalidOperationException("The server executable path is unknown.");

        return ServerRestartPlan.CreateRelaunchStartInfo(
            processPath,
            Environment.GetCommandLineArgs(),
            Environment.CurrentDirectory,
            Environment.ProcessId
        );
    }

    private static bool SpawnProcess(ProcessStartInfo startInfo)
    {
        using Process? process = Process.Start(startInfo);
        return process is not null;
    }
}
