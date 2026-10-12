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

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Networking;

namespace NoMercy.Networking.Connectivity;

/// <summary>What the API saw when it called this server back, per address family.</summary>
public sealed record ReachabilityReport(
    ReachabilityVerdict Ipv4,
    ReachabilityVerdict Ipv6,
    DateTime CheckedAtUtc
);

public interface IReachabilityChecker
{
    /// <summary>
    /// Runs the check before the host starts: a short-lived listener answers on the
    /// internal port, because Kestrel does not own it yet. The result is kept for the
    /// first connectivity evaluation.
    /// </summary>
    Task<ReachabilityReport> CheckBeforeHostStartsAsync(CancellationToken ct);

    /// <summary>
    /// Runs the check while the host serves: Kestrel answers the code itself (the
    /// plaintext sniffer in front of TLS, or the http pipeline in setup mode).
    /// </summary>
    Task<ReachabilityReport> CheckAsync(CancellationToken ct);

    /// <summary>
    /// The answer from before the host started, once, while it is still fresh. Every
    /// later evaluation asks anew, because the network may have changed since.
    /// </summary>
    ReachabilityReport? TakeBootReport();
}

public sealed class ReachabilityChecker(
    IReachabilityProbe probe,
    ILogger<ReachabilityChecker> logger,
    ReachCodes? codes = null,
    Func<int>? internalPort = null,
    Func<int>? externalPort = null,
    Func<DateTime>? now = null,
    IReadOnlyList<IPAddress>? listenAddresses = null
) : IReachabilityChecker
{
    private static readonly TimeSpan BootReportLifetime = TimeSpan.FromMinutes(2);

    private readonly ReachCodes _codes = codes ?? ReachCodes.Shared;
    private readonly Func<int> _internalPort =
        internalPort ?? (() => RuntimeServerSettings.Current.InternalServerPort);
    private readonly Func<int> _externalPort =
        externalPort ?? (() => RuntimeServerSettings.Current.ExternalServerPort);
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    private ReachabilityReport? _bootReport;

    public async Task<ReachabilityReport> CheckBeforeHostStartsAsync(CancellationToken ct)
    {
        ReachListener? listener = ReachListener.TryStart(
            _internalPort(),
            _codes,
            logger,
            listenAddresses
        );
        if (listener is null)
        {
            // Something else holds the port, so a call-back would prove something about that.
            logger.LogWarning(
                "Port {Port} is in use before the server started — skipping the reachability check until the server is up.",
                _internalPort()
            );
            return new(ReachabilityVerdict.Unknown, ReachabilityVerdict.Unknown, _now());
        }

        await using (listener)
        {
            ReachabilityReport report = await RunAsync(ct);
            _bootReport = report;
            return report;
        }
    }

    public Task<ReachabilityReport> CheckAsync(CancellationToken ct) => RunAsync(ct);

    public ReachabilityReport? TakeBootReport()
    {
        ReachabilityReport? report = Interlocked.Exchange(ref _bootReport, null);
        return report is not null && _now() - report.CheckedAtUtc < BootReportLifetime
            ? report
            : null;
    }

    private async Task<ReachabilityReport> RunAsync(CancellationToken ct)
    {
        string code = _codes.Issue();
        try
        {
            int port = _externalPort();
            Task<ReachabilityVerdict> ipv4 = probe.ProbeBackAsync(
                AddressFamily.InterNetwork,
                port,
                code,
                ct
            );
            Task<ReachabilityVerdict> ipv6 = probe.ProbeBackAsync(
                AddressFamily.InterNetworkV6,
                port,
                code,
                ct
            );

            ReachabilityReport report = new(await ipv4, await ipv6, _now());
            logger.LogInformation(
                "Reachability call-back on port {Port}: IPv4 {Ipv4}, IPv6 {Ipv6}",
                port,
                report.Ipv4,
                report.Ipv6
            );
            return report;
        }
        finally
        {
            _codes.Revoke(code);
        }
    }
}
