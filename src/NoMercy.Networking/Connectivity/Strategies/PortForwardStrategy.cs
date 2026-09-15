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
    IReachabilityProbe? reachabilityProbe = null
) : IConnectivityStrategy
{
    public string Name => "PortForward";
    public int Priority => 1;
    public ConnectivityType Type => ConnectivityType.PortForward;

    public async Task<ConnectivityResult> TryEstablishAsync(CancellationToken ct)
    {
        // Probe on every pass. A NatStatus of Open left over from an earlier evaluation
        // describes the network the server used to be on, and a re-evaluation happens
        // precisely because that network changed.
        connectivityStatus.PortForwarded = await networkDiscovery.IsPortOpenAsync();

        if (connectivityStatus.PortForwarded)
        {
            logger.LogInformation(
                "Reached the server on its own external address — port forwarding confirmed."
            );
            connectivityStatus.NatStatus = NatStatus.Open;
            return ConnectivityResult.Verified();
        }

        // Most routers refuse to hairpin, so the probe above fails on a port that is open
        // to the outside. The only check that can tell is one made from outside.
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
            return ConnectivityResult.Failed();
        }

        // The outside check did not run (no token, API down). A mapping the router accepted
        // over UPnP is then a claim, not proof: good enough to keep a working user working,
        // never good enough to outrank a transport that can prove itself.
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
        if (reachabilityProbe is null)
            return ReachabilityVerdict.Unknown;

        string address = networkDiscovery.DirectExternalAddress;
        if (string.IsNullOrEmpty(address) || address.Contains("0-0-0-0"))
            return ReachabilityVerdict.Unknown;

        return await reachabilityProbe.ProbeAsync(address, ct);
    }

    public Task TeardownAsync()
    {
        // Nothing to stop, but the flag must not outlive the strategy: another transport
        // winning while PortForwarded is still true reports a direct path that is not there.
        connectivityStatus.PortForwarded = false;
        return Task.CompletedTask;
    }
}
