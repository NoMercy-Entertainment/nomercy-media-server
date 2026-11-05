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
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Status;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Networking.Connectivity.Strategies;

/// <summary>
/// The floor of the ladder. An account-less Cloudflare quick tunnel needs no router
/// change, no token, no DNS record and no account: cloudflared dials out, Cloudflare
/// assigns a random trycloudflare.com name, and the server publishes that name. The
/// name changes on every run, which is fine because clients always fetch the current
/// address from the control plane.
///
/// The name is published a few seconds after the connection registers. Asking any caching
/// resolver for it before then poisons that resolver with a "does not exist" answer for the
/// zone's 30 minute negative TTL, and home routers cache exactly like that. So publication
/// is confirmed through Cloudflare's own DNS-over-HTTPS resolver, never the system one.
/// </summary>
public partial class QuickTunnelStrategy : IConnectivityStrategy, IDisposable
{
    private readonly ILogger<QuickTunnelStrategy> _logger;
    private readonly IConnectivityStatus _connectivityStatus;
    private readonly Func<bool> _binaryExists;
    private readonly Func<string?> _binaryStamp;
    private readonly Func<string, CancellationToken, Task<bool>> _resolves;
    private Process? _process;
    private string? _startedFromStamp;
    private volatile bool _stoppingIntentionally;
    private bool _disposed;
    private string? _assignedUrl;
    private bool _registeredOnce;
    private TaskCompletionSource<bool>? _registered;

    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PublicationGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PublicationTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PublicationPollInterval = TimeSpan.FromSeconds(3);
    private static readonly HttpClient DnsOverHttps = new() { Timeout = TimeSpan.FromSeconds(8) };

    [GeneratedRegex(
        @"https://[a-z0-9-]+\.trycloudflare\.com",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex AssignedUrlPattern();

    [GeneratedRegex(
        @"Registered tunnel connection|Connection [0-9a-f-]+ registered",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ConnectionRegisteredPattern();

    [GeneratedRegex(
        @"\bERR\b|\bFTL\b|error|failed",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FailurePattern();

    // cloudflared loops this forever with the OS process alive; checked only once
    // registered, so a normal not-yet-connected retry never trips it.
    [GeneratedRegex(
        @"Register tunnel error from server side.*Unauthorized.*Tunnel not found",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex UnrecoverablePostRegistrationFailurePattern();

    internal static bool IsUnrecoverableAfterRegistration(string line) =>
        UnrecoverablePostRegistrationFailurePattern().IsMatch(line);

    public QuickTunnelStrategy(
        ILogger<QuickTunnelStrategy> logger,
        IConnectivityStatus connectivityStatus,
        Func<bool>? binaryExists = null,
        Func<string, CancellationToken, Task<bool>>? resolves = null,
        IStorageDriver? driver = null
    )
    {
        IStorageDriver binaries = driver ?? new LocalStorageDriver();
        _logger = logger;
        _connectivityStatus = connectivityStatus;
        _binaryExists = binaryExists ?? (() => CloudflaredBinary.Exists(binaries));
        _binaryStamp = () => CloudflaredBinary.Stamp(binaries);
        _resolves = resolves ?? IsPublishedAtCloudflareAsync;
    }

    public string Name => "QuickTunnel";
    public int Priority => 4;
    public ConnectivityType Type => ConnectivityType.QuickTunnel;

    public bool IsStillEstablished => _process is { HasExited: false };

    /// <summary>
    /// True while the tunnel runs from a cloudflared the background upgrade has since
    /// replaced on disk. A missing or half-replaced file is not a verdict yet.
    /// </summary>
    public bool IsOutdated
    {
        get
        {
            string? current = _binaryStamp();
            return IsStillEstablished && current is not null && current != _startedFromStamp;
        }
    }

    /// <summary>
    /// False while cloudflared has not been downloaded yet. The manager defers this
    /// strategy rather than counting the missing binary as a failed attempt.
    /// </summary>
    public bool IsReady => _binaryExists();

    /// <summary>
    /// The assigned name in one cloudflared log line, or null. Exposed so the parse is
    /// testable without a process.
    /// </summary>
    internal static string? AssignedUrlIn(string line)
    {
        Match match = AssignedUrlPattern().Match(line);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    internal static bool IsConnectionRegistered(string line) =>
        ConnectionRegisteredPattern().IsMatch(line);

    // Logs the line, resolves registration, and self-stops on the stuck-looping
    // unauthorized rejection so SuperviseAsync's own IsStillEstablished check notices.
    internal void Watch(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        if (!_stoppingIntentionally && FailurePattern().IsMatch(line))
            _logger.LogWarning("cloudflared: {Line}", line);
        else
            _logger.LogDebug("cloudflared: {Line}", line);

        _assignedUrl ??= AssignedUrlIn(line);

        if (IsConnectionRegistered(line))
        {
            _registeredOnce = true;
            if (_assignedUrl is not null)
                _registered?.TrySetResult(true);
        }
        else if (_registeredOnce && IsUnrecoverableAfterRegistration(line))
            StopTunnel();
    }

    // Test seam: lets a test drive Watch/StopTunnel against a trivial real process.
    internal void AttachProcessForTests(Process process) => _process = process;

    public async Task<ConnectivityResult> TryEstablishAsync(CancellationToken ct)
    {
        if (!_binaryExists())
        {
            _logger.LogInformation(
                "cloudflared is not installed yet at {Path} — the quick tunnel will retry once the download finishes",
                AppFiles.CloudflareDPath
            );
            return ConnectivityResult.Failed();
        }

        _stoppingIntentionally = false;
        _assignedUrl = null;
        _registeredOnce = false;
        _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> registered = _registered;

        // A quick tunnel from a run that ended without TeardownAsync keeps a dead server's
        // name registered and, once its image is renamed by an upgrade, pins the backup.
        CloudflaredBinary.ReapOrphans(_logger, _process);

        try
        {
            // Read before Start(): a replacement landing in between then costs one
            // needless restart instead of hiding the old binary behind the new stamp.
            _startedFromStamp = _binaryStamp();
            _process = new()
            {
                StartInfo = new()
                {
                    FileName = AppFiles.CloudflareDPath,
                    // http2 over TCP: QUIC is filtered on enough home networks that the
                    // control stream registers and then dies until cloudflared gives up.
                    Arguments =
                        $"tunnel --no-autoupdate --protocol http2 --url https://127.0.0.1:{RuntimeServerSettings.Current.InternalServerPort} --no-tls-verify",
                    UseShellExecute = false,
                    WorkingDirectory = AppFiles.DependenciesPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };

            _process.OutputDataReceived += (_, args) => Watch(args.Data);
            _process.ErrorDataReceived += (_, args) => Watch(args.Data);
            _process.Exited += (_, _) =>
            {
                if (_stoppingIntentionally)
                    _logger.LogInformation("Quick tunnel process exited");
                else
                    _logger.LogWarning("Quick tunnel process exited");
                registered.TrySetResult(false);
            };

            _process.Start();
            // Bind the child to the server's kill-on-close job object so a crash or a hard
            // kill of the server — where TeardownAsync never runs — cannot leave an
            // orphaned cloudflared behind. No-op fallback off Windows.
            Shell.ProcessHelper.AttachToParentLifetime(_process);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

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
                    "The quick tunnel did not register a connection within {Seconds}s",
                    RegistrationTimeout.TotalSeconds
                );
            }

            if (!connected || _assignedUrl is null)
            {
                StopTunnel();
                return ConnectivityResult.Failed();
            }

            // Advertising the name before Cloudflare publishes it hands clients a name
            // that does not exist yet, and their resolvers remember that for 30 minutes.
            string host = new Uri(_assignedUrl).Host;
            if (!await WaitUntilPublishedAsync(host, ct))
            {
                _logger.LogWarning(
                    "The quick tunnel name {Host} was not published within {Seconds}s",
                    [host, PublicationTimeout.TotalSeconds]
                );
                StopTunnel();
                return ConnectivityResult.Failed();
            }

            _connectivityStatus.PublicUrl = _assignedUrl;
            _connectivityStatus.NatStatus = NatStatus.Tunneled;
            _logger.LogInformation("Quick tunnel is up at {Url}", _assignedUrl);
            return ConnectivityResult.Verified();
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Failed to start the quick tunnel: {Message}", ex.Message);
            StopTunnel();
            return ConnectivityResult.Failed();
        }
    }

    private async Task<bool> WaitUntilPublishedAsync(string host, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + PublicationTimeout;
        await Task.Delay(PublicationGrace, ct);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await _resolves(host, ct))
                return true;

            await Task.Delay(PublicationPollInterval, ct);
        }

        return false;
    }

    /// <summary>
    /// Asks Cloudflare's resolver over HTTPS whether the name has an address yet. It sees
    /// its own zone within seconds and this never touches the router's DNS cache.
    /// </summary>
    private static async Task<bool> IsPublishedAtCloudflareAsync(string host, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A"
            );
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));

            using HttpResponseMessage response = await DnsOverHttps.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return false;

            await using Stream body = await response.Content.ReadAsStreamAsync(ct);
            return IsPublishedAnswer(await JsonDocument.ParseAsync(body, cancellationToken: ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a DNS JSON answer carries at least one A record. Status 0 alone is not
    /// enough: a name that exists with no address is still unreachable.
    /// </summary>
    internal static bool IsPublishedAnswer(JsonDocument answer)
    {
        using (answer)
        {
            JsonElement root = answer.RootElement;
            if (!root.TryGetProperty("Status", out JsonElement status) || status.GetInt32() != 0)
                return false;

            if (!root.TryGetProperty("Answer", out JsonElement records))
                return false;

            foreach (JsonElement record in records.EnumerateArray())
            {
                if (record.TryGetProperty("type", out JsonElement type) && type.GetInt32() == 1)
                    return true;
            }

            return false;
        }
    }

    public Task TeardownAsync()
    {
        StopTunnel();
        _connectivityStatus.PublicUrl = null;
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
            if (_process is { HasExited: false })
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Shell.ProcessHelper.SendCtrlC(_process);
                else
                    _process.CloseMainWindow();

                if (!_process.WaitForExit(3000))
                    _process.Kill(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Error stopping the quick tunnel: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        StopTunnel();
        _process?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
