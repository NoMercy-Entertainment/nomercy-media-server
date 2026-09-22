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
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Status;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Networking.Connectivity.Strategies;

public partial class CloudflareTunnelStrategy : IConnectivityStrategy, IDisposable
{
    private readonly Func<Task>? _checkTunnelAvailability;
    private readonly IConnectivityStatus _connectivityStatus;
    private readonly Func<bool> _binaryExists;
    private Process? _tunnelProcess;
    private bool _disposed;

    /// <summary>
    /// Set the moment a graceful shutdown is requested. cloudflared keeps forwarding
    /// in-flight requests to an origin we are in the process of tearing down, so it logs
    /// "connection refused" and "process exited" for a few hundred milliseconds after every
    /// stop — expected noise, not a failure to surface at warning level.
    /// </summary>
    private volatile bool _stoppingIntentionally;

    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// cloudflared logs a line per edge connection it registers. That line is the first
    /// moment the tunnel can actually carry traffic, so it is what "the tunnel is up" has to
    /// mean. Both the current and the older wording are matched so a cloudflared upgrade
    /// cannot silently turn every tunnel into a timeout.
    /// </summary>
    [GeneratedRegex(
        @"registered tunnel connection|connection .* registered",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ConnectionRegisteredPattern();

    [GeneratedRegex(@"\bERR\b|\bFTL\b|error|failed|unauthorized|expired", RegexOptions.IgnoreCase)]
    private static partial Regex FailurePattern();

    /// <summary>
    /// cloudflared logs a client that walks away at ERR: a browser navigating off a page
    /// cancels its in-flight API calls, and a player switching quality or seeking abandons
    /// the HLS segments it no longer needs. Both are the client's own choice and the tunnel
    /// stays healthy, but every one matched FailurePattern and surfaced as a warning, so a
    /// normal evening of watching filled the log with red that nobody can act on. Only the
    /// graceful shapes are matched — an HTTP/2 cancel carries error code 0, and a non-zero
    /// code is still a real stream failure.
    /// </summary>
    [GeneratedRegex(
        @"ended abruptly: context canceled|canceled by remote with error code 0\b|\bcontext canceled\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ClientCancellationPattern();

    internal static bool IsClientCancellation(string line)
    {
        return ClientCancellationPattern().IsMatch(line);
    }

    /// <summary>
    /// Decides the log level for one raw cloudflared output line. Split out from the process
    /// event handler so the stopping-intentionally gate — the actual fix for the shutdown log
    /// spam — is exercisable without spawning a real cloudflared process in tests.
    /// </summary>
    internal void LogProcessLine(string line)
    {
        // cloudflared reports why it failed — bad token, clock skew, no egress — on
        // these streams. At LogTrace none of it was ever written, so every tunnel
        // failure looked identical and unexplained from the logs.
        if (FailurePattern().IsMatch(line) && !IsClientCancellation(line))
        {
            if (_stoppingIntentionally)
                _logger.LogDebug("cloudflared: {Line}", line);
            else
                _logger.LogWarning("cloudflared: {Line}", line);
        }
        else
            _logger.LogDebug("cloudflared: {Line}", line);
    }

    /// <summary>
    /// cloudflared exits on a revoked token, a clock skew, or a network drop. Nothing noticed
    /// before: the exit was logged and the manager kept reporting Tunneled for the rest of
    /// the server's uptime while nothing was listening.
    /// </summary>
    public bool IsStillEstablished => _tunnelProcess is { HasExited: false };

    /// <summary>
    /// False only when a tunnel is assigned but the binary that runs it has not landed on
    /// disk yet. cloudflared is downloaded at runtime, sixth in a sequential queue, so on an
    /// early boot this is routinely still false for a few seconds — that is a "not yet", not
    /// evidence the tunnel does not work. Reads the same file-presence check TryEstablishAsync
    /// uses, so there is exactly one definition of "the binary is here" in this strategy.
    /// </summary>
    public bool IsReady =>
        string.IsNullOrEmpty(_connectivityStatus.CloudflareTunnelToken) || _binaryExists();

    public string Name => "CloudflareTunnel";
    public int Priority => 3;
    public ConnectivityType Type => ConnectivityType.CloudflareTunnel;

    private readonly ILogger<CloudflareTunnelStrategy> _logger;

    public CloudflareTunnelStrategy(
        ILogger<CloudflareTunnelStrategy> logger,
        IConnectivityStatus connectivityStatus,
        Func<Task>? checkTunnelAvailability = null,
        Func<bool>? binaryExists = null
    )
    {
        _logger = logger;
        _checkTunnelAvailability = checkTunnelAvailability;
        _connectivityStatus = connectivityStatus;
        _binaryExists = binaryExists ?? (() => File.Exists(AppFiles.CloudflareDPath));
    }

    public async Task<ConnectivityResult> TryEstablishAsync(CancellationToken ct)
    {
        if (_checkTunnelAvailability is not null)
            await _checkTunnelAvailability();

        if (string.IsNullOrEmpty(_connectivityStatus.CloudflareTunnelToken))
        {
            switch (_connectivityStatus.TunnelAvailability)
            {
                case TunnelAvailability.CheckFailed:
                    _logger.LogWarning(
                        "Could not check whether a Cloudflare tunnel is available for this server — will retry."
                    );
                    return ConnectivityResult.Failed();

                case TunnelAvailability.Provisioning:
                    _logger.LogInformation(
                        "A Cloudflare tunnel is being provisioned for this server — will retry once it is ready."
                    );
                    return ConnectivityResult.Failed();
            }

            // No named tunnel is the normal case for a free/non-paying server, not a
            // problem: QuickTunnel is tried next and covers this on every real
            // network this runs on. Logging port-forward guidance here fired on
            // every single boot regardless of outcome, right before QuickTunnel's
            // own success line — read as a real warning when there was nothing to
            // fix. The guidance now belongs where a failure is actually real: when
            // no strategy at all works, in ConnectivityManager's local-only branch.
            _logger.LogDebug(
                "No Cloudflare tunnel is set up for this server — trying the next strategy."
            );
            return ConnectivityResult.Failed();
        }

        // The binary is downloaded at runtime, sixth in a sequential queue that includes
        // ffmpeg, so on an early boot it is routinely not there yet. Starting it anyway threw
        // a Win32Exception that read like a generic failure; saying plainly that the download
        // has not landed is the difference between "retry shortly" and "this is broken". The
        // manager already defers this strategy via IsReady while the binary is missing; this
        // check remains as the same source of truth for the forced attempt once the bounded
        // deferral window elapses.
        if (!_binaryExists())
        {
            _logger.LogInformation(
                "Cloudflare tunnel is assigned but cloudflared is not installed yet at {Path} — will retry once the download finishes",
                AppFiles.CloudflareDPath
            );
            return ConnectivityResult.Failed();
        }

        // A cloudflared from a previous run of this server can still be alive: a crash, a
        // container kill or any non-graceful exit skips TeardownAsync, and the child keeps
        // running. Starting a second one registers a second connector against the same
        // tunnel, so every ungraceful restart permanently adds another set of edge
        // connections, and an orphan carries the ingress config it started with — it can
        // still be routing traffic to an origin this server has since moved.
        ReapOrphanedTunnels();

        try
        {
            _tunnelProcess = new()
            {
                StartInfo = new()
                {
                    FileName = AppFiles.CloudflareDPath,
                    // QUIC (cloudflared's default edge transport) is unstable on some
                    // networks — UDP filtering/NAT causes the control stream to
                    // register then repeatedly die with "control stream encountered a
                    // failure while serving" until cloudflared gives up after its 30s
                    // registration window and this server falls back to PortForward
                    // (often itself a false-positive: UPnP reports success without the
                    // router actually honouring the mapping). http2 runs over a normal
                    // TCP/TLS connection and is cloudflared's documented mitigation for
                    // this exact symptom.
                    Arguments = "tunnel --protocol http2 run",
                    // Pass the tunnel token via environment variable rather than the
                    // command line so it is not exposed in /proc/<pid>/cmdline or via
                    // WMI Win32_Process.CommandLine. cloudflared reads TUNNEL_TOKEN.
                    Environment = { ["TUNNEL_TOKEN"] = _connectivityStatus.CloudflareTunnelToken },
                    UseShellExecute = false,
                    WorkingDirectory = AppFiles.DependenciesPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };

            // A started process is not a working tunnel. cloudflared exits non-fatally on a
            // revoked token, a clock skew or no egress, and reporting success on Start()
            // left the server advertising a tunnel address nothing was listening on.
            TaskCompletionSource<bool> registered = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            void Watch(string? line)
            {
                if (string.IsNullOrEmpty(line))
                    return;

                LogProcessLine(line);

                if (ConnectionRegisteredPattern().IsMatch(line))
                    registered.TrySetResult(true);
            }

            _tunnelProcess.OutputDataReceived += (_, args) => Watch(args.Data);
            _tunnelProcess.ErrorDataReceived += (_, args) => Watch(args.Data);
            _tunnelProcess.Exited += (_, _) =>
            {
                if (_stoppingIntentionally)
                    _logger.LogInformation("Cloudflare tunnel process exited");
                else
                    _logger.LogWarning("Cloudflare tunnel process exited");
                registered.TrySetResult(false);
            };

            _tunnelProcess.Start();
            _tunnelProcess.BeginOutputReadLine();
            _tunnelProcess.BeginErrorReadLine();

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                ct
            );
            timeout.CancelAfter(RegistrationTimeout);

            bool connected;
            try
            {
                connected = await registered.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                connected = false;
                _logger.LogWarning(
                    "Cloudflare tunnel did not register a connection within {Seconds}s",
                    RegistrationTimeout.TotalSeconds
                );
            }

            if (!connected)
            {
                StopTunnel();
                return ConnectivityResult.Failed();
            }

            _connectivityStatus.NatStatus = NatStatus.Tunneled;
            _logger.LogInformation("Cloudflare tunnel registered a connection");
            return ConnectivityResult.Verified();
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Failed to start Cloudflare tunnel: {Message}", ex.Message);
            StopTunnel();
            return ConnectivityResult.Failed();
        }
    }

    /// <summary>
    /// Kills any cloudflared started from our own binary path that is not the process this
    /// strategy currently owns. Matching on the executable path rather than the name alone
    /// keeps a cloudflared the user runs for their own tunnels out of scope.
    /// </summary>
    internal void ReapOrphanedTunnels()
    {
        string ourPath;
        try
        {
            ourPath = Path.GetFullPath(AppFiles.CloudflareDPath);
        }
        catch
        {
            return;
        }

        foreach (Process candidate in Process.GetProcessesByName("cloudflared"))
            using (candidate)
            {
                try
                {
                    if (_tunnelProcess is not null && candidate.Id == _tunnelProcess.Id)
                        continue;

                    string? path = candidate.MainModule?.FileName;
                    if (
                        path is null
                        || !string.Equals(
                            Path.GetFullPath(path),
                            ourPath,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;

                    _logger.LogWarning(
                        "Found an orphaned cloudflared from a previous run (pid {Pid}) — stopping it so this server registers one connector, not two",
                        candidate.Id
                    );

                    candidate.Kill(true);
                    candidate.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    // MainModule throws for processes we cannot inspect; that just means it
                    // is not ours to reap.
                    _logger.LogDebug("Skipped cloudflared pid inspection: {Message}", ex.Message);
                }
            }
    }

    public Task TeardownAsync()
    {
        StopTunnel();
        return Task.CompletedTask;
    }

    public void BeginShutdown()
    {
        _stoppingIntentionally = true;
    }

    private void StopTunnel()
    {
        _stoppingIntentionally = true;
        try
        {
            if (_tunnelProcess is { HasExited: false })
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Shell.ProcessHelper.SendCtrlC(_tunnelProcess);
                else
                    _tunnelProcess.CloseMainWindow();

                if (!_tunnelProcess.WaitForExit(3000))
                    _tunnelProcess.Kill(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Error stopping Cloudflare tunnel: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        StopTunnel();
        _tunnelProcess?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
