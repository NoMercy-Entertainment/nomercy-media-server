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
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Configuration;

namespace NoMercy.Service.Hosting;

public class PortManager : IPortManager
{
    private readonly ILogger<PortManager> _logger;
    private readonly ICertificateService _certificateService;

    /// <summary>
    /// The address the availability probe binds. Production probes the wildcard,
    /// because that is the address the server will bind. Tests pass loopback so a
    /// test run never asks the Windows firewall.
    /// </summary>
    public IPAddress ProbeAddress { get; }

    public PortManager(
        ILogger<PortManager> logger,
        ICertificateService certificateService,
        IPAddress? probeAddress = null
    )
    {
        _logger = logger;
        _certificateService = certificateService;
        ProbeAddress = probeAddress ?? IPAddress.Any;
    }

    public async Task EnsurePortAvailable(int port)
    {
        if (IsPortAvailable(port))
            return;

        _logger.LogInformation("Port {Port} is in use — checking for stale instances...", port);
        string processInfo = await FindProcessOnPortAsync(port);

        if (!string.IsNullOrEmpty(processInfo))
            _logger.LogInformation(
                "Process holding port {Port}:\n{ProcessInfo}",
                [port, processInfo]
            );

        int blockingPid = ParsePidFromPortInfo(processInfo);

        if (blockingPid <= 0)
        {
            if (_certificateService.HasValidCertificate())
            {
                _logger.LogError(
                    "Port {Port} is in use by an unknown process. NoMercy is registered on this port and cannot use a different one. Free the port and restart.",
                    port
                );
            }
            else
            {
                _logger.LogError(
                    "Port {Port} is in use but cannot identify the process. Please free it manually.",
                    port
                );
            }

            throw new StartupAbortException(
                $"Port {port} is in use and the blocking process could not be identified."
            );
        }

        bool isStaleInstance = false;
        string blockingProcessName = "unknown";
        try
        {
            Process blockingProcess = Process.GetProcessById(blockingPid);
            blockingProcessName = blockingProcess.ProcessName;
            isStaleInstance = blockingProcessName == "NoMercyMediaServer";
        }
        catch
        {
            // Process may have exited between detection and lookup
        }

        if (isStaleInstance)
        {
            _logger.LogInformation(
                "Stale NoMercyMediaServer instance detected (PID {BlockingPid}). Auto-killing...",
                blockingPid
            );
        }
        else
        {
            bool isRegistered = _certificateService.HasValidCertificate();

            if (isRegistered)
            {
                _logger.LogError(
                    "Port {Port} is in use by {BlockingProcessName} (PID {BlockingPid}). NoMercy is registered on this port and cannot use a different one. Free the port and restart.",
                    [port, blockingProcessName, blockingPid]
                );
                throw new StartupAbortException(
                    $"Port {port} is in use by {blockingProcessName} (PID {blockingPid}) and NoMercy is registered on this port."
                );
            }

            int alternativePort = FindNextAvailablePort(port + 1);
            _logger.LogInformation(
                "Port {Port} is in use by {BlockingProcessName} (PID {BlockingPid}). Server is not yet registered — using port {AlternativePort} instead.",
                [port, blockingProcessName, blockingPid, alternativePort]
            );
            RuntimeServerSettings.Current.InternalServerPort = alternativePort;
            return;
        }

        bool portFreed = await KillAndWaitAsync(blockingPid, port);

        if (!portFreed)
        {
            throw new StartupAbortException(
                $"Port {port} could not be freed after killing the stale process (PID {blockingPid})."
            );
        }

        _logger.LogInformation("Port freed — continuing startup...");
    }

    public int FindNextAvailablePort(int startPort)
    {
        const int MaxPort = 65535;

        for (int candidate = startPort; candidate <= MaxPort; candidate++)
        {
            if (IsPortAvailable(candidate))
                return candidate;
        }

        _logger.LogError(
            "No available port found in range {StartPort}–{MaxPort}.",
            [startPort, MaxPort]
        );
        throw new StartupAbortException($"No available port found in range {startPort}-{MaxPort}.");
    }

    public bool IsPortAvailable(int port)
    {
        try
        {
            using TcpListener listener = new(ProbeAddress, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking port availability for {Port}", port);
            return false;
        }
    }

    public async Task<bool> HandlePortInUse(int port, IOException ex)
    {
        if (
            ex.InnerException is not SocketException socketEx
            || socketEx.SocketErrorCode != SocketError.AddressAlreadyInUse
        )
        {
            return false;
        }

        _logger.LogWarning(
            "Host failed to bind to port {Port} (Address already in use). Attempting recovery...",
            port
        );
        await EnsurePortAvailable(port);

        // If EnsurePortAvailable didn't exit the process, we can retry on the same port
        // (if it was freed) or we already switched RuntimeServerSettings.Current.InternalServerPort.
        return true;
    }

    private async Task<bool> KillAndWaitAsync(int pid, int port)
    {
        try
        {
            Process process = Process.GetProcessById(pid);
            process.Kill();

            _logger.LogInformation(
                "Sent kill signal to PID {Pid}. Waiting for port {Port} to be freed...",
                [pid, port]
            );

            // Wait up to 5 seconds for the port to be freed
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(100);
                if (IsPortAvailable(port))
                    return true;
            }

            _logger.LogError(
                "Timed out waiting for port {Port} to be freed by PID {Pid}.",
                [port, pid]
            );
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to kill process {Pid} or wait for port {Port}.",
                [pid, port]
            );
            return false;
        }
    }

    private async Task<string> FindProcessOnPortAsync(int port)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: netstat -ano | findstr :<port>
            try
            {
                using Process process = new();
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.Arguments =
                    $"/c \"netstat -ano | findstr LISTENING | findstr :{port}\"";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.Start();

                return await process.StandardOutput.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to run netstat on Windows");
                return string.Empty;
            }
        }

        if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        )
        {
            // Linux/macOS: lsof -i :<port>
            try
            {
                using Process process = new();
                process.StartInfo.FileName = "lsof";
                process.StartInfo.Arguments = $"-i :{port}";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.Start();

                return await process.StandardOutput.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to run lsof on Linux/macOS");
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private int ParsePidFromPortInfo(string processInfo)
    {
        if (string.IsNullOrWhiteSpace(processInfo))
            return -1;

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ParsePidFromNetstat(processInfo)
            : ParsePidFromLsof(processInfo);
    }

    // netstat -ano output: the last whitespace-delimited column of a LISTENING
    // row is the owning PID.
    // Example: TCP    0.0.0.0:7625    0.0.0.0:0    LISTENING    1234
    internal static int ParsePidFromNetstat(string processInfo)
    {
        if (string.IsNullOrWhiteSpace(processInfo))
            return -1;

        string[] lines = processInfo.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries
        );
        if (lines.Length > 0)
        {
            string[] parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && int.TryParse(parts[^1], out int pid))
                return pid;
        }

        return -1;
    }

    // lsof -i output: the second column of the first data row (after the
    // header line) is the owning PID.
    internal static int ParsePidFromLsof(string processInfo)
    {
        if (string.IsNullOrWhiteSpace(processInfo))
            return -1;

        string[] lines = processInfo.Split('\n');
        if (lines.Length > 1)
        {
            string[] parts = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && int.TryParse(parts[1], out int pid))
                return pid;
        }

        return -1;
    }
}
