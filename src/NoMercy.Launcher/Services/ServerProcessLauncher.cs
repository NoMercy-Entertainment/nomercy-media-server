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
using System.Reflection;
using NoMercy.NmSystem.FileSystem;
using NoMercy.NmSystem.Information;

namespace NoMercy.Launcher.Services;

public class ServerProcessLauncher
{
    private Process? _serverProcess;
    private Process? _appProcess;

    public bool IsServerProcessRunning => _serverProcess is { HasExited: false };

    public bool IsAppProcessRunning => _appProcess is { HasExited: false };

    public Process? ServerProcess => _serverProcess;

    public Task<bool> StartServerAsync(string? extraArguments = null)
    {
        if (IsServerProcessRunning)
            return Task.FromResult(false);

        // Wipe stale log files so the new run's logs aren't buried under
        // the previous execution's. Best-effort: any file held by another
        // process (a stray Rider-launched server, the CLI's logs SSE
        // stream) is skipped, the start still proceeds.
        ClearLogsDirectory();

        // Prefer the binary next to the Launcher (installer deployment),
        // then fall back to the binaries path (standalone deployment)
        ProcessStartInfo? startInfo =
            CreateInstalledStartInfo()
            ?? CreateProductionStartInfo()
            ?? CreateDevBinaryStartInfo()
            ?? CreateDotnetRunStartInfo();

        if (startInfo is null)
            return Task.FromResult(false);

        // Append user-configured startup arguments
        if (!string.IsNullOrWhiteSpace(extraArguments))
        {
            foreach (string arg in ParseArguments(extraArguments))
                startInfo.ArgumentList.Add(arg);
        }

        // Tell the server it's running from an installed deployment so it
        // skips binary downloads (the installer handles updates)
        string? installDir = GetInstallDirectory();
        if (installDir is not null)
            startInfo.Environment["NOMERCY_INSTALL_DIR"] = installDir;

        _serverProcess = new() { StartInfo = startInfo, EnableRaisingEvents = true };

        _serverProcess.Exited += (sender, _) => OnServerProcessExited(sender);

        bool started = _serverProcess.Start();
        return Task.FromResult(started);
    }

    public Task<bool> LaunchAppAsync(string? route = null)
    {
        if (IsAppProcessRunning)
            return Task.FromResult(false);

        ProcessStartInfo? startInfo =
            CreateAppInstalledStartInfo()
            ?? CreateAppProductionStartInfo()
            ?? CreateAppDevBinaryStartInfo()
            ?? CreateAppDotnetRunStartInfo();

        if (startInfo is null)
            return Task.FromResult(false);

        if (!string.IsNullOrEmpty(route))
        {
            startInfo.ArgumentList.Add("--route");
            startInfo.ArgumentList.Add(route);
        }

        _appProcess = new() { StartInfo = startInfo, EnableRaisingEvents = true };

        _appProcess.Exited += (sender, _) => OnAppProcessExited(sender);

        bool started = _appProcess.Start();

        if (!started)
        {
            _appProcess = null;
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public async Task<bool> WaitForServerExitAsync(TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);

        if (_serverProcess is not null)
        {
            try
            {
                await _serverProcess.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        // Server wasn't started by the Tray — poll until it's gone
        while (!cts.Token.IsCancellationRequested)
        {
            if (!IsServerProcessRunning)
                return true;

            try
            {
                await Task.Delay(500, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return !IsServerProcessRunning;
            }
        }

        return !IsServerProcessRunning;
    }

    private void OnServerProcessExited(object? sender)
    {
        if (sender is Process exitedProcess)
            Interlocked.CompareExchange(ref _serverProcess, null, exitedProcess);
    }

    private void OnAppProcessExited(object? sender)
    {
        if (sender is Process exitedProcess)
            Interlocked.CompareExchange(ref _appProcess, null, exitedProcess);
    }

    public Task ForceKillServerAsync()
    {
        try
        {
            if (_serverProcess is { HasExited: false })
            {
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess = null;
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Error("Force kill failed", ex);
        }

        return Task.CompletedTask;
    }

    public async Task ApplyUpdateAsync()
    {
        string tempPath = AppFiles.ServerTempExePath;
        string currentPath = AppFiles.ServerExePath;
        string backupPath = currentPath + ".bak";

        LauncherLog.Info($"ApplyUpdate: temp={tempPath}, current={currentPath}");

        if (!File.Exists(tempPath))
        {
            LauncherLog.Error($"Staged update not found at {tempPath}");
            throw new FileNotFoundException("No staged update found.", tempPath);
        }

        // Backup current binary before replacing
        if (File.Exists(currentPath))
        {
            LauncherLog.Info($"Backing up current binary to {backupPath}");
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            File.Move(currentPath, backupPath);
        }

        try
        {
            File.Move(tempPath, currentPath);
            await FilePermissions.SetExecutionPermissions(currentPath);
            LauncherLog.Info("Binary replacement successful");

            // Clean up backup on success
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("Binary replacement failed, attempting rollback", ex);

            // Rollback: restore backup if move failed
            if (File.Exists(backupPath) && !File.Exists(currentPath))
            {
                File.Move(backupPath, currentPath);
                LauncherLog.Info("Rollback successful");
            }

            throw;
        }
    }

    public async Task ApplyUpdateIfStagedAsync()
    {
        string tempPath = AppFiles.ServerTempExePath;

        LauncherLog.Info(
            $"Checking for staged update at {tempPath}: exists={File.Exists(tempPath)}"
        );

        if (File.Exists(tempPath))
            await ApplyUpdateAsync();
    }

    /// <summary>
    /// Deletes every <c>log*.txt</c> in <see cref="AppFiles.LogPath"/> so
    /// the next server run starts with a clean slate. Files held by another
    /// process are skipped — the start path stays unblocked.
    /// </summary>
    private static void ClearLogsDirectory()
    {
        try
        {
            string logDir = AppFiles.LogPath;
            if (!Directory.Exists(logDir))
                return;

            foreach (string file in Directory.EnumerateFiles(logDir, "log*.txt"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Locked by a still-running server / CLI — leave it.
                }
                catch (UnauthorizedAccessException)
                {
                    // Permission denied — skip rather than crash the launch.
                }
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Error("Failed to clear log directory before server start", ex);
        }
    }

    /// <summary>
    /// Returns the Launcher's directory only if it's an installer deployment
    /// (i.e., running from a different directory than the binaries path).
    /// Returns null for standalone deployments where everything is in the binaries path.
    /// </summary>
    private static string? GetInstallDirectory()
    {
        string? ownDir = Path.GetDirectoryName(
            Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location
        );

        if (ownDir is null)
            return null;

        // If the Launcher is in the binaries path, this is a standalone deployment
        if (
            string.Equals(
                Path.GetFullPath(ownDir),
                Path.GetFullPath(AppFiles.BinariesPath),
                StringComparison.OrdinalIgnoreCase
            )
        )
            return null;

        return ownDir;
    }

    private static ProcessStartInfo? CreateProductionStartInfo()
    {
        string exePath = AppFiles.ServerExePath;

        if (!File.Exists(exePath))
            return null;

        return new(exePath) { UseShellExecute = false, CreateNoWindow = true };
    }

    private static ProcessStartInfo? CreateInstalledStartInfo()
    {
        string? ownDir = Path.GetDirectoryName(
            Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location
        );

        if (ownDir is null)
            return null;

        string candidate = Path.Combine(ownDir, "NoMercyMediaServer" + Info.ExecSuffix);

        if (!File.Exists(candidate))
            return null;

        return new(candidate) { UseShellExecute = false, CreateNoWindow = true };
    }

    private static ProcessStartInfo? CreateDevBinaryStartInfo()
    {
        string? serverBinary = FindServerBinary();

        if (serverBinary is null)
            return null;

        ProcessStartInfo startInfo = new(serverBinary)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--dev");

        return startInfo;
    }

    private static ProcessStartInfo? CreateDotnetRunStartInfo()
    {
        string? serverProjectDir = FindServerProjectDirectory();

        if (serverProjectDir is null)
            return null;

        ProcessStartInfo startInfo = new("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(serverProjectDir);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--dev");

        return startInfo;
    }

    private static string? FindServerBinary()
    {
        string? serverProjectDir = FindServerProjectDirectory();

        if (serverProjectDir is null)
            return null;

        string execName = "NoMercyMediaServer" + Info.ExecSuffix;

        string[] searchPaths =
        [
            Path.Combine(
                serverProjectDir,
                "bin",
                "Debug",
                $"net{Environment.Version.Major}.{Environment.Version.Minor}",
                execName
            ),
            Path.Combine(
                serverProjectDir,
                "bin",
                "Release",
                $"net{Environment.Version.Major}.{Environment.Version.Minor}",
                execName
            ),
        ];

        foreach (string path in searchPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static ProcessStartInfo? CreateAppProductionStartInfo()
    {
        string exePath = AppFiles.AppExePath;

        if (!File.Exists(exePath))
            return null;

        return new(exePath) { UseShellExecute = false, CreateNoWindow = true };
    }

    private static ProcessStartInfo? CreateAppInstalledStartInfo()
    {
        string? ownDir = Path.GetDirectoryName(
            Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location
        );

        if (ownDir is null)
            return null;

        string candidate = Path.Combine(ownDir, "NoMercyApp" + Info.ExecSuffix);

        if (!File.Exists(candidate))
            return null;

        return new(candidate) { UseShellExecute = false, CreateNoWindow = true };
    }

    private static ProcessStartInfo? CreateAppDevBinaryStartInfo()
    {
        string? appProjectDir = FindProjectDirectory("NoMercy.App");

        if (appProjectDir is null)
            return null;

        string execName = "NoMercyApp" + Info.ExecSuffix;

        string[] searchPaths =
        [
            Path.Combine(
                appProjectDir,
                "bin",
                "Debug",
                $"net{Environment.Version.Major}.{Environment.Version.Minor}",
                execName
            ),
            Path.Combine(
                appProjectDir,
                "bin",
                "Release",
                $"net{Environment.Version.Major}.{Environment.Version.Minor}",
                execName
            ),
        ];

        foreach (string path in searchPaths)
        {
            if (File.Exists(path))
                return new(path) { UseShellExecute = false, CreateNoWindow = true };
        }

        return null;
    }

    private static ProcessStartInfo? CreateAppDotnetRunStartInfo()
    {
        string? appProjectDir = FindProjectDirectory("NoMercy.App");

        if (appProjectDir is null)
            return null;

        ProcessStartInfo startInfo = new("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(appProjectDir);

        return startInfo;
    }

    private static string? FindServerProjectDirectory()
    {
        return FindProjectDirectory("NoMercy.Service");
    }

    private static string? FindProjectDirectory(string projectName)
    {
        string? assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        string? directory = assemblyLocation;

        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "src", projectName);

            if (Directory.Exists(candidate))
                return candidate;

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>
    /// Splits a command-line string into individual arguments,
    /// respecting double-quoted segments.
    /// </summary>
    internal static List<string> ParseArguments(string input)
    {
        List<string> args = [];
        int i = 0;

        while (i < input.Length)
        {
            // Skip whitespace
            while (i < input.Length && char.IsWhiteSpace(input[i]))
                i++;

            if (i >= input.Length)
                break;

            if (input[i] == '"')
            {
                // Quoted argument
                i++;
                int start = i;
                while (i < input.Length && input[i] != '"')
                    i++;

                args.Add(input[start..i]);

                if (i < input.Length)
                    i++; // skip closing quote
            }
            else
            {
                // Unquoted argument
                int start = i;
                while (i < input.Length && !char.IsWhiteSpace(input[i]))
                    i++;

                args.Add(input[start..i]);
            }
        }

        return args;
    }
}
