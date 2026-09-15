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
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Status;
using NoMercy.Storage.Drivers.Local;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed class ConnectivityManagerStateTests : IDisposable
{
    private readonly ConnectivityMode _originalMode = RuntimeServerSettings
        .Current
        .ConnectivityMode;

    // Every fixture here exercises strategy evaluation, which requires Auto —
    // RuntimeServerSettings.Current is a process-wide static, and its default is now
    // LocalOnly (a fresh install must not attempt remote connectivity unasked).
    public ConnectivityManagerStateTests()
    {
        RuntimeServerSettings.Current.ConnectivityMode = ConnectivityMode.Auto;
    }

    public void Dispose()
    {
        RuntimeServerSettings.Current.ConnectivityMode = _originalMode;
    }

    private static NetworkDiscovery BuildNetworkDiscovery()
    {
        NetworkDiscovery d = new(
            NullLogger<NetworkDiscovery>.Instance,
            new LocalStorageDriver(),
            new AuthTokenStore(),
            new ConnectivityStatus(),
            new()
        );
        d.ExternalIp = "1.2.3.4";
        return d;
    }

    private sealed class StubStrategy(
        string name,
        int priority,
        ConnectivityType type,
        bool succeeds,
        ConnectivityConfidence confidence = ConnectivityConfidence.Verified
    ) : IConnectivityStrategy
    {
        public string Name => name;
        public int Priority => priority;
        public ConnectivityType Type => type;
        public bool WasAttempted { get; private set; }
        public int AttemptCount { get; private set; }
        public int TeardownCount { get; private set; }

        public Task<ConnectivityResult> TryEstablishAsync(CancellationToken ct)
        {
            WasAttempted = true;
            AttemptCount++;
            return Task.FromResult(
                succeeds ? new ConnectivityResult(true, confidence) : ConnectivityResult.Failed()
            );
        }

        public Task TeardownAsync()
        {
            TeardownCount++;
            return Task.CompletedTask;
        }
    }

    private static ConnectivityManager BuildManager(params IConnectivityStrategy[] strategies)
    {
        return BuildManager(null, new ConnectivityStatus(), strategies);
    }

    private static ConnectivityManager BuildManager(
        Func<Task>? tunnelAvailability,
        params IConnectivityStrategy[] strategies
    )
    {
        return BuildManager(tunnelAvailability, new ConnectivityStatus(), strategies);
    }

    private static ConnectivityManager BuildManager(
        Func<Task>? tunnelAvailability,
        ConnectivityStatus connectivityStatus,
        params IConnectivityStrategy[] strategies
    )
    {
        AuthTokenStore tokenStore = new();
        tokenStore.SetAccessToken("test-token");
        BootStatus boot = new();
        boot.MarkStarted();

        return BuildManager(tunnelAvailability, connectivityStatus, null, strategies);
    }

    private static ConnectivityManager BuildManager(
        Func<Task>? tunnelAvailability,
        ConnectivityStatus connectivityStatus,
        Func<Task>? transportChanged,
        params IConnectivityStrategy[] strategies
    )
    {
        AuthTokenStore tokenStore = new();
        tokenStore.SetAccessToken("test-token");
        BootStatus boot = new();
        boot.MarkStarted();

        return new(
            NullLogger<ConnectivityManager>.Instance,
            tokenStore,
            BuildNetworkDiscovery(),
            strategies,
            boot,
            connectivityStatus,
            tunnelAvailability,
            transportChanged: transportChanged
        );
    }

    [Fact]
    public async Task EvaluateAsync_WhenFirstStrategySucceeds_SetsDirectAccessState()
    {
        StubStrategy winning = new("PortForward", 1, ConnectivityType.PortForward, succeeds: true);
        StubStrategy skipped = new("Stun", 2, ConnectivityType.StunHolePunch, succeeds: true);
        ConnectivityManager manager = BuildManager(winning, skipped);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.DirectAccess, manager.CurrentState);
    }

    [Fact]
    public async Task EvaluateAsync_WhenAllStrategiesFail_SetsLocalOnlyState()
    {
        StubStrategy a = new("PortForward", 1, ConnectivityType.PortForward, succeeds: false);
        StubStrategy b = new("Stun", 2, ConnectivityType.StunHolePunch, succeeds: false);
        ConnectivityManager manager = BuildManager(a, b);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.LocalOnly, manager.CurrentState);
    }

    [Fact]
    public async Task EvaluateAsync_WhenNoStrategies_SetsLocalOnlyState()
    {
        ConnectivityManager manager = BuildManager();

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.LocalOnly, manager.CurrentState);
    }

    [Fact]
    public async Task EvaluateAsync_FirstStrategyFails_SecondSucceeds_SetsHolePunchedState()
    {
        StubStrategy fail = new("PortForward", 1, ConnectivityType.PortForward, succeeds: false);
        StubStrategy win = new("Stun", 2, ConnectivityType.StunHolePunch, succeeds: true);
        ConnectivityManager manager = BuildManager(fail, win);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.HolePunched, manager.CurrentState);
        Assert.True(fail.WasAttempted);
        Assert.True(win.WasAttempted);
    }

    [Fact]
    public async Task EvaluateAsync_CloudflareTunnelSucceeds_SetsTunneledState()
    {
        StubStrategy cf = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: true
        );
        ConnectivityManager manager = BuildManager(cf);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.Tunneled, manager.CurrentState);
    }

    [Fact]
    public async Task EvaluateAsync_FirstSuccessWins_LaterStrategyNotAttempted()
    {
        StubStrategy first = new("PortForward", 1, ConnectivityType.PortForward, succeeds: true);
        StubStrategy second = new("Stun", 2, ConnectivityType.StunHolePunch, succeeds: true);
        ConnectivityManager manager = BuildManager(first, second);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.True(first.WasAttempted);
        Assert.False(second.WasAttempted);
    }

    [Fact]
    public async Task EvaluateAsync_StateChangedEvent_FiresWithCorrectState()
    {
        StubStrategy strategy = new("PortForward", 1, ConnectivityType.PortForward, succeeds: true);
        ConnectivityManager manager = BuildManager(strategy);
        List<ConnectivityState> observed = [];
        manager.StateChanged += s => observed.Add(s);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Contains(ConnectivityState.DirectAccess, observed);
    }

    [Fact]
    public async Task EvaluateAsync_AllFail_ActiveStrategyIsLocalOnly()
    {
        StubStrategy fail = new("PortForward", 1, ConnectivityType.PortForward, succeeds: false);
        ConnectivityManager manager = BuildManager(fail);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityType.LocalOnly, manager.ActiveStrategy);
    }

    [Fact]
    public async Task EvaluateAsync_StrategiesOrderedByPriority_LowerPriorityTriedFirst()
    {
        List<string> attemptOrder = [];

        OrderTrackingStrategy low = new("Low", 1, ConnectivityType.PortForward, attemptOrder);
        OrderTrackingStrategy high = new("High", 2, ConnectivityType.StunHolePunch, attemptOrder);

        ConnectivityManager manager = BuildManager(high, low);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal("Low", attemptOrder[0]);
        Assert.Equal("High", attemptOrder[1]);
    }

    private sealed class OrderTrackingStrategy(
        string name,
        int priority,
        ConnectivityType type,
        List<string> log
    ) : IConnectivityStrategy
    {
        public string Name => name;
        public int Priority => priority;
        public ConnectivityType Type => type;

        public Task<ConnectivityResult> TryEstablishAsync(CancellationToken ct)
        {
            log.Add(name);
            return Task.FromResult(ConnectivityResult.Failed());
        }

        public Task TeardownAsync() => Task.CompletedTask;
    }

    // ── Confidence ranking ──────────────────────────────────────────────────
    //
    // The defect these cover: a UPnP mapping the router accepted but never honoured used to
    // report plain success from priority 1, so the loop returned before it ever reached the
    // tunnel the user had been assigned. Disabling NAT and deleting the firewall rule made
    // no difference, because neither changes whether the SOAP call throws.

    [Fact]
    public async Task EvaluateAsync_UnverifiedStrategy_DoesNotStopAVerifiedOneFromWinning()
    {
        StubStrategy assumed = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true,
            ConnectivityConfidence.Assumed
        );
        StubStrategy verified = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: true
        );
        ConnectivityManager manager = BuildManager(assumed, verified);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.Tunneled, manager.CurrentState);
        Assert.Equal(ConnectivityType.CloudflareTunnel, manager.ActiveStrategy);
    }

    [Fact]
    public async Task EvaluateAsync_UnverifiedStrategy_IsTornDownWhileBetterOnesAreTried()
    {
        StubStrategy assumed = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true,
            ConnectivityConfidence.Assumed
        );
        StubStrategy verified = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: true
        );
        ConnectivityManager manager = BuildManager(assumed, verified);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(1, assumed.TeardownCount);
    }

    [Fact]
    public async Task EvaluateAsync_UnverifiedStrategy_IsUsedWhenNothingElseSucceeds()
    {
        StubStrategy assumed = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true,
            ConnectivityConfidence.Assumed
        );
        StubStrategy failing = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: false
        );
        ConnectivityManager manager = BuildManager(assumed, failing);

        await manager.EvaluateAsync(CancellationToken.None);

        // Never break the users whose router genuinely forwards but refuses to hairpin:
        // unverified still wins over going local-only, it just no longer wins over proof.
        Assert.Equal(ConnectivityState.DirectAccess, manager.CurrentState);
        Assert.Equal(ConnectivityType.PortForward, manager.ActiveStrategy);
        Assert.Equal(2, assumed.AttemptCount);
    }

    // ── Tunnel availability ─────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_AsksWhetherATunnelIsAssigned_BeforeTryingAnyStrategy()
    {
        List<string> order = [];
        StubStrategy first = new("PortForward", 1, ConnectivityType.PortForward, succeeds: true);
        ConnectivityManager manager = BuildManager(
            () =>
            {
                order.Add("availability");
                return Task.CompletedTask;
            },
            new OrderTrackingStrategy("PortForward", 1, ConnectivityType.PortForward, order)
        );

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal("availability", order[0]);
        Assert.False(first.WasAttempted);
    }

    [Fact]
    public async Task EvaluateAsync_WhenTheAvailabilityCheckThrows_StillEvaluatesStrategies()
    {
        StubStrategy strategy = new("PortForward", 1, ConnectivityType.PortForward, succeeds: true);
        ConnectivityManager manager = BuildManager(
            () => throw new InvalidOperationException("API unreachable"),
            strategy
        );

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.True(strategy.WasAttempted);
        Assert.Equal(ConnectivityState.DirectAccess, manager.CurrentState);
    }

    // ── An assigned tunnel is an instruction ────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_WhenATunnelIsAssigned_DirectPathsAreStillTriedFirst()
    {
        List<string> order = [];
        ConnectivityStatus status = new() { CloudflareTunnelToken = "assigned-token" };
        ConnectivityManager manager = BuildManager(
            null,
            status,
            new OrderTrackingStrategy("PortForward", 1, ConnectivityType.PortForward, order),
            new OrderTrackingStrategy(
                "CloudflareTunnel",
                3,
                ConnectivityType.CloudflareTunnel,
                order
            )
        );

        await manager.EvaluateAsync(CancellationToken.None);

        // A token is a fallback the operator paid for, not an instruction to ignore a
        // router that forwards. Putting the tunnel first sent every client through the
        // Cloudflare edge while the port forward answered from outside.
        Assert.Equal("PortForward", order[0]);
    }

    [Fact]
    public async Task EvaluateAsync_AVerifiedPortForward_WinsOverAnAssignedTunnel()
    {
        ConnectivityStatus status = new() { CloudflareTunnelToken = "assigned-token" };
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true
        );
        StubStrategy tunnel = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: true
        );
        ConnectivityManager manager = BuildManager(null, status, portForward, tunnel);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.DirectAccess, manager.CurrentState);
        Assert.False(tunnel.WasAttempted);
        Assert.Equal("port_forward", status.Transport);
    }

    [Fact]
    public async Task EvaluateAsync_AnAssignedTunnel_WinsOverAnUnverifiedPortForward()
    {
        ConnectivityStatus status = new() { CloudflareTunnelToken = "assigned-token" };
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true,
            ConnectivityConfidence.Assumed
        );
        StubStrategy tunnel = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: true
        );
        ConnectivityManager manager = BuildManager(null, status, portForward, tunnel);

        await manager.EvaluateAsync(CancellationToken.None);

        // A mapping nothing could confirm from outside is only a fallback; a tunnel that
        // registered with the edge is proof.
        Assert.Equal(ConnectivityState.Tunneled, manager.CurrentState);
        Assert.Equal("tunnel", status.Transport);
    }

    [Fact]
    public async Task EvaluateAsync_ReportsTheTransport_TheMomentItIsDecided()
    {
        int reports = 0;
        ConnectivityStatus status = new();
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true
        );
        ConnectivityManager manager = BuildManager(
            null,
            status,
            () =>
            {
                reports++;
                return Task.CompletedTask;
            },
            portForward
        );

        await manager.EvaluateAsync(CancellationToken.None);

        // The control plane publishes the address clients use, so it has to learn the
        // decision now, not on the next IP change.
        Assert.Equal(1, reports);
        Assert.Equal("port_forward", status.Transport);
    }

    [Fact]
    public async Task EvaluateAsync_AQuickTunnel_IsTheFloor_AndReportsQuickTunnel()
    {
        ConnectivityStatus status = new();
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: false
        );
        StubStrategy named = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: false
        );
        StubStrategy quick = new("QuickTunnel", 4, ConnectivityType.QuickTunnel, succeeds: true);
        ConnectivityManager manager = BuildManager(null, status, quick, named, portForward);

        await manager.EvaluateAsync(CancellationToken.None);

        // Nothing above it verified, so the floor carries the server. The control plane
        // publishes the assigned name because the transport says so.
        Assert.Equal(ConnectivityState.Tunneled, manager.CurrentState);
        Assert.Equal(ConnectivityType.QuickTunnel, manager.ActiveStrategy);
        Assert.Equal("quick_tunnel", status.Transport);
        Assert.True(portForward.WasAttempted);
        Assert.True(named.WasAttempted);
    }

    [Fact]
    public async Task EvaluateAsync_AVerifiedPortForward_IsNeverReplacedByTheQuickTunnel()
    {
        ConnectivityStatus status = new();
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true
        );
        StubStrategy quick = new("QuickTunnel", 4, ConnectivityType.QuickTunnel, succeeds: true);
        ConnectivityManager manager = BuildManager(null, status, quick, portForward);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.DirectAccess, manager.CurrentState);
        Assert.False(quick.WasAttempted);
    }

    [Fact]
    public async Task EvaluateAsync_WhenNothingWorks_ReportsLocal()
    {
        ConnectivityStatus status = new() { Transport = "tunnel" };
        StubStrategy failing = new("PortForward", 1, ConnectivityType.PortForward, succeeds: false);
        ConnectivityManager manager = BuildManager(null, status, failing);

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.LocalOnly, manager.CurrentState);
        Assert.Equal("local", status.Transport);
    }

    [Fact]
    public async Task EvaluateAsync_WhenTheTransportReportThrows_StillActivates()
    {
        ConnectivityStatus status = new();
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true
        );
        ConnectivityManager manager = BuildManager(
            null,
            status,
            () => throw new InvalidOperationException("API unreachable"),
            portForward
        );

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.DirectAccess, manager.CurrentState);
        Assert.Equal("port_forward", status.Transport);
    }

    [Fact]
    public async Task EvaluateAsync_WhenNoTunnelIsAssigned_KeepsThePriorityOrder()
    {
        List<string> order = [];
        ConnectivityManager manager = BuildManager(
            null,
            new ConnectivityStatus(),
            new OrderTrackingStrategy("PortForward", 1, ConnectivityType.PortForward, order),
            new OrderTrackingStrategy(
                "CloudflareTunnel",
                3,
                ConnectivityType.CloudflareTunnel,
                order
            )
        );

        await manager.EvaluateAsync(CancellationToken.None);

        Assert.Equal("PortForward", order[0]);
    }

    [Fact]
    public async Task EvaluateAsync_WhenAnAssignedTunnelFails_StillFallsBackToAnUnverifiedPortForward()
    {
        ConnectivityStatus status = new() { CloudflareTunnelToken = "assigned-token" };
        StubStrategy tunnel = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: false
        );
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true,
            ConnectivityConfidence.Assumed
        );
        ConnectivityManager manager = BuildManager(null, status, portForward, tunnel);

        await manager.EvaluateAsync(CancellationToken.None);

        // A tunnel that cannot come up must not strand a server whose router probably
        // forwards; the unverified mapping is still better than local-only.
        Assert.True(tunnel.WasAttempted);
        Assert.Equal(ConnectivityState.DirectAccess, manager.CurrentState);
    }

    // ── Pinned mode ─────────────────────────────────────────────────────────
    //
    // The supported replacement for editing the strategy list by hand to make a server stop
    // trying to port forward. Tests live in this class so they serialise against the other
    // readers of the process-wide RuntimeServerSettings.Current.

    private static async Task WithMode(ConnectivityMode mode, Func<Task> body)
    {
        ConnectivityMode previous = RuntimeServerSettings.Current.ConnectivityMode;
        RuntimeServerSettings.Current.ConnectivityMode = mode;
        try
        {
            await body();
        }
        finally
        {
            RuntimeServerSettings.Current.ConnectivityMode = previous;
        }
    }

    [Fact]
    public async Task EvaluateAsync_ModePinnedToTunnel_SkipsPortForwardEntirely()
    {
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true
        );
        StubStrategy tunnel = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: true
        );
        ConnectivityManager manager = BuildManager(portForward, tunnel);

        await WithMode(
            ConnectivityMode.CloudflareTunnel,
            async () =>
            {
                await manager.EvaluateAsync(CancellationToken.None);

                Assert.False(portForward.WasAttempted);
                Assert.True(tunnel.WasAttempted);
                Assert.Equal(ConnectivityState.Tunneled, manager.CurrentState);
            }
        );
    }

    [Fact]
    public async Task EvaluateAsync_ModePinnedToTunnel_GoesLocalOnlyRatherThanFallingBack()
    {
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true
        );
        StubStrategy tunnel = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: false
        );
        ConnectivityManager manager = BuildManager(portForward, tunnel);

        await WithMode(
            ConnectivityMode.CloudflareTunnel,
            async () =>
            {
                await manager.EvaluateAsync(CancellationToken.None);

                // Pinned means pinned. Quietly reverting to the transport the operator ruled
                // out is how they lose trust in the setting.
                Assert.False(portForward.WasAttempted);
                Assert.Equal(ConnectivityState.LocalOnly, manager.CurrentState);
            }
        );
    }

    [Fact]
    public async Task EvaluateAsync_ModePinnedToLocalOnly_TriesNothing()
    {
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: true
        );
        ConnectivityManager manager = BuildManager(portForward);

        await WithMode(
            ConnectivityMode.LocalOnly,
            async () =>
            {
                await manager.EvaluateAsync(CancellationToken.None);

                Assert.False(portForward.WasAttempted);
                Assert.Equal(ConnectivityState.LocalOnly, manager.CurrentState);
            }
        );
    }

    [Fact]
    public async Task EvaluateAsync_ModePinnedToPortForward_SkipsTheTunnel()
    {
        StubStrategy portForward = new(
            "PortForward",
            1,
            ConnectivityType.PortForward,
            succeeds: false
        );
        StubStrategy tunnel = new(
            "CloudflareTunnel",
            3,
            ConnectivityType.CloudflareTunnel,
            succeeds: true
        );
        ConnectivityManager manager = BuildManager(portForward, tunnel);

        await WithMode(
            ConnectivityMode.PortForward,
            async () =>
            {
                await manager.EvaluateAsync(CancellationToken.None);

                Assert.True(portForward.WasAttempted);
                Assert.False(tunnel.WasAttempted);
                Assert.Equal(ConnectivityState.LocalOnly, manager.CurrentState);
            }
        );
    }
}
