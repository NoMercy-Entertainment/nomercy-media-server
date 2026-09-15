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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Connectivity;
using NoMercy.Networking.Connectivity.Strategies;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Networking;
using NoMercy.NmSystem.Status;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed class PortForwardStrategyTests
{
    private sealed class StubNetworkDiscovery : INetworkDiscovery
    {
        private readonly bool _portOpen;

        public StubNetworkDiscovery(bool portOpen = false)
        {
            _portOpen = portOpen;
        }

        public string InternalIp { get; set; } = "192.168.1.1";
        public string RegistrationInternalIp => InternalIp;
        public string ExternalIp { get; set; } = "1.2.3.4";
        public string? InternalIpV6 => null;
        public string? ExternalIpV6 { get; set; }
        public string InternalDomain => string.Empty;
        public string InternalAddress => string.Empty;
        public string ExternalDomain => string.Empty;
        public string ExternalAddress => string.Empty;
        public string DirectExternalAddress => $"https://{ExternalIp.Replace('.', '-')}.stub:7626";
        public string? ExternalAddressV6 => null;
        public bool Ipv6Enabled => false;

        public Task DiscoverExternalIpAsync() => Task.CompletedTask;

        public Task ForceRediscoveryAsync() => Task.CompletedTask;

        public Task<bool> IsPortOpenAsync() => Task.FromResult(_portOpen);

        public Task RemovePortMappingsAsync() => Task.CompletedTask;
    }

    private sealed class FakeProbe(ReachabilityVerdict verdict) : IReachabilityProbe
    {
        public string? ProbedUrl { get; private set; }

        public Task<ReachabilityVerdict> ProbeAsync(string url, CancellationToken ct)
        {
            ProbedUrl = url;
            return Task.FromResult(verdict);
        }
    }

    private static PortForwardStrategy BuildStrategy(
        ConnectivityStatus status,
        bool portOpen = false
    )
    {
        return new(
            new StubNetworkDiscovery(portOpen),
            status,
            NullLogger<PortForwardStrategy>.Instance
        );
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTheProbeConnects_IsVerified()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.None };
        PortForwardStrategy strategy = BuildStrategy(status, portOpen: true);

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.True(result.Established);
        Assert.Equal(ConnectivityConfidence.Verified, result.Confidence);
        Assert.Equal(NatStatus.Open, status.NatStatus);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenUpnpMappedButTheProbeFails_IsOnlyAssumed()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        PortForwardStrategy strategy = BuildStrategy(status, portOpen: false);

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        // A router that accepts the UPnP call and drops the mapping is indistinguishable
        // from one that forwards correctly but will not hairpin. Reporting the first as
        // established fact is what pinned servers to a port forward that did not exist.
        Assert.True(result.Established);
        Assert.Equal(ConnectivityConfidence.Assumed, result.Confidence);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenUpnpMappedButUnproven_DoesNotClaimNatIsOpen()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        PortForwardStrategy strategy = BuildStrategy(status, portOpen: false);

        await strategy.TryEstablishAsync(CancellationToken.None);

        // NatStatus is reported to the API as stun_nat_type. Promoting an unconfirmed
        // mapping to Open told the control plane the server was directly reachable.
        Assert.Equal(NatStatus.Filtered, status.NatStatus);
    }

    [Fact]
    public async Task TryEstablishAsync_AlwaysProbes_EvenWhenAnEarlierPassLeftNatOpen()
    {
        TrackingNetworkDiscovery tracking = new();
        ConnectivityStatus status = new() { NatStatus = NatStatus.Open };
        PortForwardStrategy strategy = new(
            tracking,
            status,
            NullLogger<PortForwardStrategy>.Instance
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        // A stale Open describes the network the server used to be on, and re-evaluation
        // happens precisely because that network changed.
        Assert.True(tracking.IsPortOpenCalled);
        Assert.False(result.Established);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenNatStatusIsNone_AndPortClosed_Fails()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.None };
        PortForwardStrategy strategy = BuildStrategy(status, portOpen: false);

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.False(result.Established);
        Assert.Equal(ConnectivityConfidence.None, result.Confidence);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenNatStatusIsClosed_AndPortClosed_Fails()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Closed };
        PortForwardStrategy strategy = BuildStrategy(status, portOpen: false);

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.False(result.Established);
    }

    [Fact]
    public async Task TeardownAsync_ClearsPortForwarded_SoAnotherTransportDoesNotInheritIt()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        PortForwardStrategy strategy = BuildStrategy(status, portOpen: false);

        await strategy.TryEstablishAsync(CancellationToken.None);
        Assert.True(status.PortForwarded);

        await strategy.TeardownAsync();

        Assert.False(status.PortForwarded);
    }

    [Fact]
    public void Priority_IsOne()
    {
        PortForwardStrategy strategy = BuildStrategy(new());

        Assert.Equal(1, strategy.Priority);
    }

    [Fact]
    public void Type_IsPortForward()
    {
        PortForwardStrategy strategy = BuildStrategy(new());

        Assert.Equal(ConnectivityType.PortForward, strategy.Type);
    }

    [Fact]
    public void Name_IsPortForward()
    {
        PortForwardStrategy strategy = BuildStrategy(new());

        Assert.Equal("PortForward", strategy.Name);
    }

    [Fact]
    public async Task TeardownAsync_DoesNotThrow_AndCompletes()
    {
        PortForwardStrategy strategy = BuildStrategy(new());

        Exception? ex = await Record.ExceptionAsync(strategy.TeardownAsync);

        Assert.Null(ex);
    }

    private sealed class TrackingNetworkDiscovery : INetworkDiscovery
    {
        public bool IsPortOpenCalled { get; private set; }
        public string InternalIp { get; set; } = "192.168.1.1";
        public string RegistrationInternalIp => InternalIp;
        public string ExternalIp { get; set; } = "1.2.3.4";
        public string? InternalIpV6 => null;
        public string? ExternalIpV6 { get; set; }
        public string InternalDomain => string.Empty;
        public string InternalAddress => string.Empty;
        public string ExternalDomain => string.Empty;
        public string ExternalAddress => string.Empty;
        public string DirectExternalAddress => $"https://{ExternalIp.Replace('.', '-')}.stub:7626";
        public string? ExternalAddressV6 => null;
        public bool Ipv6Enabled => false;

        public Task DiscoverExternalIpAsync() => Task.CompletedTask;

        public Task ForceRediscoveryAsync() => Task.CompletedTask;

        public Task<bool> IsPortOpenAsync()
        {
            IsPortOpenCalled = true;
            return Task.FromResult(false);
        }

        public Task RemovePortMappingsAsync() => Task.CompletedTask;
    }

    // ── Verified from outside ───────────────────────────────────────────────
    //
    // Most routers refuse to hairpin, so the in-LAN probe fails on a port that is open to
    // the world. The cloud probe is the only check that can tell that apart from closed.

    [Fact]
    public async Task TryEstablishAsync_WhenTheCloudReachesTheServer_IsVerified()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        FakeProbe probe = new(ReachabilityVerdict.Reachable);
        PortForwardStrategy strategy = new(
            new StubNetworkDiscovery(portOpen: false),
            status,
            NullLogger<PortForwardStrategy>.Instance,
            probe
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.Equal(ConnectivityConfidence.Verified, result.Confidence);
        Assert.Equal(NatStatus.Open, status.NatStatus);
        Assert.True(status.PortForwarded);
        Assert.Equal("https://1-2-3-4.stub:7626", probe.ProbedUrl);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTheCloudCannotReachTheServer_FailsEvenWithAUpnpMapping()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        PortForwardStrategy strategy = new(
            new StubNetworkDiscovery(portOpen: false),
            status,
            NullLogger<PortForwardStrategy>.Instance,
            new FakeProbe(ReachabilityVerdict.Unreachable)
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        // The router said yes to the mapping and the outside said no. The outside is right;
        // holding this as a fallback would advertise an address that nothing can reach.
        Assert.False(result.Established);
        Assert.False(status.PortForwarded);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTheCloudCheckCouldNotRun_FallsBackToTheUpnpClaim()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        PortForwardStrategy strategy = new(
            new StubNetworkDiscovery(portOpen: false),
            status,
            NullLogger<PortForwardStrategy>.Instance,
            new FakeProbe(ReachabilityVerdict.Unknown)
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        // An API outage is not evidence about the router.
        Assert.True(result.Established);
        Assert.Equal(ConnectivityConfidence.Assumed, result.Confidence);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTheHairpinProbeAlreadyConnected_DoesNotAskTheCloud()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.None };
        FakeProbe probe = new(ReachabilityVerdict.Unreachable);
        PortForwardStrategy strategy = new(
            new StubNetworkDiscovery(portOpen: true),
            status,
            NullLogger<PortForwardStrategy>.Instance,
            probe
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.Equal(ConnectivityConfidence.Verified, result.Confidence);
        Assert.Null(probe.ProbedUrl);
    }

    [Fact]
    public async Task TryEstablishAsync_WithNoPublicAddressYet_DoesNotAskTheCloud()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        FakeProbe probe = new(ReachabilityVerdict.Reachable);
        StubNetworkDiscovery discovery = new(portOpen: false) { ExternalIp = "0.0.0.0" };
        PortForwardStrategy strategy = new(
            discovery,
            status,
            NullLogger<PortForwardStrategy>.Instance,
            probe
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        // Probing 0-0-0-0 asks the API to reach nothing; the honest answer is "unknown".
        Assert.Null(probe.ProbedUrl);
        Assert.Equal(ConnectivityConfidence.Assumed, result.Confidence);
    }
}
