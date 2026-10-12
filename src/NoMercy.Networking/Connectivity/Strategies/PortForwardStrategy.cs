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
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Networking;
using NoMercy.NmSystem.Status;

namespace NoMercy.Networking.Connectivity.Strategies;

public class PortForwardStrategy(
    INetworkDiscovery networkDiscovery,
    IConnectivityStatus connectivityStatus,
    ILogger<PortForwardStrategy> logger,
    IReachabilityChecker? reachabilityChecker = null
) : IConnectivityStrategy
{
    public string Name => "PortForward";
    public int Priority => 1;
    public ConnectivityType Type => ConnectivityType.PortForward;

    public async Task<ConnectivityResult> TryEstablishAsync(CancellationToken ct)
    {
        // Clients get a name built from the external address. When that address is IPv6,
        // every client with only IPv4 cannot reach it, whatever any check says, and the
        // self-check proves nothing either: the address sits on the server's own network
        // card, so the connect never leaves the machine (2026-10-12: a firewalled IPv6
        // server claimed a port forward and the quick tunnel never started).
        if (
            IPAddress.TryParse(networkDiscovery.ExternalIp, out IPAddress? external)
            && external.AddressFamily == AddressFamily.InterNetworkV6
        )
        {
            logger.LogInformation(
                "The external address is IPv6, which clients with only IPv4 cannot reach — leaving remote access to the tunnel."
            );
            connectivityStatus.PortForwarded = false;
            return ConnectivityResult.Failed();
        }

        // Only a call from our side proves the server is reachable. The server's own connect
        // to its external address proves nothing: most routers refuse to hairpin, so it
        // fails on ports that are open.
        ReachabilityVerdict verdict = await ProbeFromOutsideAsync(ct);

        if (verdict is ReachabilityVerdict.Reachable)
        {
            logger.LogInformation(
                "The NoMercy API reached this server on {Address} — port forwarding confirmed from outside.",
                networkDiscovery.DirectExternalAddress
            );
            connectivityStatus.PortForwarded = true;
            connectivityStatus.NatStatus = NatStatus.Open;
            return ConnectivityResult.Verified();
        }

        if (verdict is ReachabilityVerdict.Unreachable)
        {
            logger.LogInformation(
                "The NoMercy API could not reach this server on {Address} — no working port forward.",
                networkDiscovery.DirectExternalAddress
            );
            connectivityStatus.PortForwarded = false;
            return ConnectivityResult.Failed();
        }

        // The outside check did not run (no token, API down). The server's own connect and a
        // mapping the router accepted over UPnP are then claims, not proof: good enough to keep
        // a working user working, never good enough to outrank a transport that can prove
        // itself. Probe on every pass: a NatStatus of Open left over from an earlier evaluation
        // describes the network the server used to be on.
        connectivityStatus.PortForwarded = await networkDiscovery.IsPortOpenAsync();

        if (connectivityStatus.PortForwarded)
        {
            logger.LogInformation(
                "Reached the server on its own external address, but nothing could confirm it from outside — treating port forwarding as unverified."
            );
            return ConnectivityResult.Assumed();
        }

        if (connectivityStatus.NatStatus == NatStatus.Filtered)
        {
            logger.LogInformation(
                "UPnP reports a port mapping but nothing could confirm it from outside — treating port forwarding as unverified."
            );
            connectivityStatus.PortForwarded = true;
            return ConnectivityResult.Assumed();
        }

        logger.LogDebug(
            "No port forward found — nothing answered on the external address and no UPnP mapping was made."
        );
        return ConnectivityResult.Failed();
    }

    private async Task<ReachabilityVerdict> ProbeFromOutsideAsync(CancellationToken ct)
    {
        if (reachabilityChecker is null)
            return ReachabilityVerdict.Unknown;

        string address = networkDiscovery.DirectExternalAddress;
        if (string.IsNullOrEmpty(address) || address.Contains("0-0-0-0"))
            return ReachabilityVerdict.Unknown;

        // The first evaluation after start reuses the answer from before the host started;
        // every later one asks again, because the network may have changed since.
        ReachabilityReport report =
            reachabilityChecker.TakeBootReport() ?? await reachabilityChecker.CheckAsync(ct);
        return report.Ipv4;
    }

    public Task TeardownAsync()
    {
        // Nothing to stop, but the flag must not outlive the strategy: another transport
        // winning while PortForwarded is still true reports a direct path that is not there.
        connectivityStatus.PortForwarded = false;
        return Task.CompletedTask;
    }
}
