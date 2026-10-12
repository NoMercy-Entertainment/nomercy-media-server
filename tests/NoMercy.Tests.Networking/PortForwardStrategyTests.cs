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

    private sealed class FakeChecker(
        ReachabilityVerdict ipv4,
        ReachabilityVerdict ipv6 = ReachabilityVerdict.Unknown,
        ReachabilityReport? bootReport = null
    ) : IReachabilityChecker
    {
        public int Checks { get; private set; }

        public ReachabilityReport? TakeBootReport()
        {
            ReachabilityReport? report = bootReport;
            bootReport = null;
            return report;
        }

        public Task<ReachabilityReport> CheckAsync(CancellationToken ct)
        {
            Checks++;
            return Task.FromResult(new ReachabilityReport(ipv4, ipv6, DateTime.UtcNow));
        }

        public Task<ReachabilityReport> CheckBeforeHostStartsAsync(CancellationToken ct) =>
            CheckAsync(ct);
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
    public async Task TryEstablishAsync_WhenOnlyTheSelfCheckConnects_IsOnlyAssumed()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.None };
        PortForwardStrategy strategy = BuildStrategy(status, portOpen: true);

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        // No call from our side ran, so a connect to the server's own address is a claim:
        // good enough as a fallback, never good enough to outrank a tunnel that proves itself.
        Assert.True(result.Established);
        Assert.Equal(ConnectivityConfidence.Assumed, result.Confidence);
        Assert.Equal(NatStatus.None, status.NatStatus);
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
    // the world. Only the API calling the server back can tell that apart from closed.

    [Fact]
    public async Task TryEstablishAsync_WhenTheApiReachesTheServerOverIpv4_IsVerified()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        FakeChecker checker = new(ReachabilityVerdict.Reachable);
        PortForwardStrategy strategy = new(
            new StubNetworkDiscovery(portOpen: false),
            status,
            NullLogger<PortForwardStrategy>.Instance,
            checker
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.Equal(ConnectivityConfidence.Verified, result.Confidence);
        Assert.Equal(NatStatus.Open, status.NatStatus);
        Assert.True(status.PortForwarded);
        Assert.Equal(1, checker.Checks);
    }

    [Fact]
    public async Task TryEstablishAsync_UsesTheAnswerFromBeforeTheServerStarted_Once()
    {
        // The boot check ran on a listener before Kestrel owned the port. Asking again on
        // the first evaluation would only repeat it; later evaluations must ask anew.
        ConnectivityStatus status = new() { NatStatus = NatStatus.None };
        FakeChecker checker = new(
            ReachabilityVerdict.Unreachable,
            bootReport: new(
                ReachabilityVerdict.Reachable,
                ReachabilityVerdict.Unknown,
                DateTime.UtcNow
            )
        );
        PortForwardStrategy strategy = new(
            new StubNetworkDiscovery(portOpen: false),
            status,
            NullLogger<PortForwardStrategy>.Instance,
            checker
        );

        ConnectivityResult first = await strategy.TryEstablishAsync(CancellationToken.None);
        ConnectivityResult second = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.Equal(ConnectivityConfidence.Verified, first.Confidence);
        Assert.False(second.Established);
        Assert.Equal(1, checker.Checks);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTheApiCannotReachTheServer_FailsEvenWithAUpnpMapping()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        PortForwardStrategy strategy = new(
            new StubNetworkDiscovery(portOpen: false),
            status,
            NullLogger<PortForwardStrategy>.Instance,
            new FakeChecker(ReachabilityVerdict.Unreachable)
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        // The router said yes to the mapping and the outside said no. The outside is right;
        // holding this as a fallback would advertise an address that nothing can reach.
        Assert.False(result.Established);
        Assert.False(status.PortForwarded);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTheApiCheckCouldNotRun_FallsBackToTheUpnpClaim()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        PortForwardStrategy strategy = new(
            new StubNetworkDiscovery(portOpen: false),
            status,
            NullLogger<PortForwardStrategy>.Instance,
            new FakeChecker(ReachabilityVerdict.Unknown)
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        // An API outage is not evidence about the router.
        Assert.True(result.Established);
        Assert.Equal(ConnectivityConfidence.Assumed, result.Confidence);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTheSelfCheckConnects_TheApiStillDecides()
    {
        // The self-check connects to the server's own public address. Most routers refuse
        // to hairpin, and an address on the server's own network card never leaves the
        // machine, so the connect proves nothing. Only a call from our side does.
        ConnectivityStatus status = new() { NatStatus = NatStatus.None };
        FakeChecker checker = new(ReachabilityVerdict.Unreachable);
        PortForwardStrategy strategy = new(
            new StubNetworkDiscovery(portOpen: true),
            status,
            NullLogger<PortForwardStrategy>.Instance,
            checker
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.Equal(1, checker.Checks);
        Assert.False(result.Established);
        Assert.False(status.PortForwarded);
    }

    [Theory]
    [InlineData(ReachabilityVerdict.Reachable)]
    [InlineData(ReachabilityVerdict.Unknown)]
    public async Task TryEstablishAsync_WhenTheAdvertisedAddressIsIpv6_LeavesItToTheTunnel(
        ReachabilityVerdict ipv4
    )
    {
        // An IPv6 server (no NAT) has its public address on its own network card, so the
        // self-check always connects. Trusting it marked a firewalled server as
        // port-forwarded and the quick tunnel never started (2026-10-12). And the name
        // clients get is built from that IPv6 address, which every IPv4-only client
        // cannot reach, whatever the IPv4 call-back said.
        ConnectivityStatus status = new() { NatStatus = NatStatus.None };
        PortForwardStrategy strategy = new(
            new StubNetworkDiscovery(portOpen: true) { ExternalIp = "2001:db8:1:2:3:4:5:6" },
            status,
            NullLogger<PortForwardStrategy>.Instance,
            new FakeChecker(ipv4, ReachabilityVerdict.Reachable)
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.False(result.Established);
        Assert.False(status.PortForwarded);
    }

    [Fact]
    public async Task TryEstablishAsync_WithNoPublicAddressYet_DoesNotAskTheApi()
    {
        ConnectivityStatus status = new() { NatStatus = NatStatus.Filtered };
        FakeChecker checker = new(ReachabilityVerdict.Reachable);
        StubNetworkDiscovery discovery = new(portOpen: false) { ExternalIp = "0.0.0.0" };
        PortForwardStrategy strategy = new(
            discovery,
            status,
            NullLogger<PortForwardStrategy>.Instance,
            checker
        );

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        // The advertised name would be 0-0-0-0, which reaches nothing; a pass proves nothing.
        Assert.Equal(0, checker.Checks);
        Assert.Equal(ConnectivityConfidence.Assumed, result.Confidence);
    }
}
