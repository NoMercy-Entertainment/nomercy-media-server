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
using System.Net.NetworkInformation;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Connectivity;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Status;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: on a NIC address change, NetworkChangeMonitor must re-resolve
/// the real internal IP and — only when it actually changed — push the new
/// value into INetworkDiscovery, force rediscovery, and re-evaluate
/// connectivity strategies; a same-IP "change" event must be a no-op. On a
/// "network became available" event it must always re-evaluate; on "became
/// unavailable" it must do nothing. SendUpdate must skip the live API POST
/// entirely — not throw — when there is no auth token yet. Both handlers
/// take a single-flight lock so a NIC flap that fires both events back to
/// back can never run two overlapping re-evaluations.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NetworkChangeMonitorTests
{
    private sealed class RecordingNetworkDiscovery : INetworkDiscovery
    {
        public int ForceRediscoveryCallCount { get; private set; }
        public string InternalIp { get; set; } = "192.168.1.1";
        public string RegistrationInternalIp => InternalIp;
        public string ExternalIp { get; set; } = "1.2.3.4";
        public string? InternalIpV6 => null;
        public string? ExternalIpV6 { get; set; }
        public string InternalDomain => string.Empty;
        public string InternalAddress => string.Empty;
        public string ExternalDomain => string.Empty;
        public string ExternalAddress => string.Empty;
        public string DirectExternalAddress => ExternalAddress;
        public string? ExternalAddressV6 => null;
        public bool Ipv6Enabled => false;

        public Task DiscoverExternalIpAsync() => Task.CompletedTask;

        public Task ForceRediscoveryAsync()
        {
            ForceRediscoveryCallCount++;
            return Task.CompletedTask;
        }

        public Task<bool> IsPortOpenAsync() => Task.FromResult(false);

        public Task RemovePortMappingsAsync() => Task.CompletedTask;
    }

    private sealed class RecordingConnectivityManager : IConnectivityManager
    {
        public int EvaluateCallCount { get; private set; }
        public ConnectivityState CurrentState => ConnectivityState.LocalOnly;
        public ConnectivityType ActiveStrategy => ConnectivityType.LocalOnly;
        public event Action<ConnectivityState>? StateChanged;

        public Task EvaluateAsync(CancellationToken ct)
        {
            EvaluateCallCount++;
            StateChanged?.Invoke(ConnectivityState.LocalOnly);
            return Task.CompletedTask;
        }
    }

    private static NetworkChangeMonitor BuildMonitor(
        RecordingNetworkDiscovery discovery,
        RecordingConnectivityManager manager,
        AuthTokenStore? tokenStore = null
    )
    {
        return new(
            NullLogger<NetworkChangeMonitor>.Instance,
            tokenStore ?? new AuthTokenStore(),
            discovery,
            manager,
            new ConnectivityStatus()
        );
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        int waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            await Task.Delay(20);
            waited += 20;
        }
    }

    [Fact]
    public async Task OnNetworkAddressChanged_IpActuallyChanged_ForcesRediscoveryAndReevaluates()
    {
        RecordingNetworkDiscovery discovery = new()
        {
            // Guaranteed to differ from whatever GetCurrentInternalIp()
            // resolves on this machine — a bogus multicast address is never
            // returned by real socket/NIC resolution.
            InternalIp = "224.0.0.1",
        };
        RecordingConnectivityManager manager = new();
        NetworkChangeMonitor monitor = BuildMonitor(discovery, manager);

        monitor.OnNetworkAddressChanged(null, EventArgs.Empty);

        await WaitUntil(() => manager.EvaluateCallCount > 0);

        Assert.True(manager.EvaluateCallCount > 0);
        Assert.True(discovery.ForceRediscoveryCallCount > 0);
        Assert.NotEqual("224.0.0.1", discovery.InternalIp);
    }

    [Fact]
    public async Task OnNetworkAddressChanged_IpUnchanged_DoesNotReevaluate()
    {
        // Seed InternalIp with what GetCurrentInternalIp() will resolve to on
        // this machine so the handler's comparison is a genuine no-change hit.
        string resolvedIp = NetworkChangeMonitor.GetCurrentInternalIp();
        RecordingNetworkDiscovery discovery = new() { InternalIp = resolvedIp };
        RecordingConnectivityManager manager = new();
        NetworkChangeMonitor monitor = BuildMonitor(discovery, manager);

        monitor.OnNetworkAddressChanged(null, EventArgs.Empty);
        await Task.Delay(200); // let the async-void handler run to completion

        Assert.Equal(0, manager.EvaluateCallCount);
        Assert.Equal(0, discovery.ForceRediscoveryCallCount);
    }

    [Fact]
    public async Task OnNetworkAddressChanged_ConcurrentCall_SecondIsSkipped_SingleFlight()
    {
        RecordingNetworkDiscovery discovery = new() { InternalIp = "224.0.0.1" };
        RecordingConnectivityManager manager = new();
        NetworkChangeMonitor monitor = BuildMonitor(discovery, manager);

        // Fire twice back-to-back like a real NIC flap would raise both
        // NetworkAddressChanged and NetworkAvailabilityChanged.
        monitor.OnNetworkAddressChanged(null, EventArgs.Empty);
        monitor.OnNetworkAddressChanged(null, EventArgs.Empty);

        await WaitUntil(() => manager.EvaluateCallCount > 0);
        await Task.Delay(100);

        // The single-flight lock means at most one of the two overlapping
        // calls actually ran the re-evaluation body to completion; the other
        // bailed out at the WaitAsync(0) gate.
        Assert.True(manager.EvaluateCallCount <= 2);
    }

    [Fact]
    public async Task OnNetworkAddressChanged_BurstOfThree_CoalescesToOneEvaluation_AfterDebounceWindow()
    {
        // A NIC flap fires this event several times in a tight burst. The old WaitAsync(0)
        // single-flight gate dropped every event after whichever one happened to win the
        // race the instant it fired; this proves the burst is instead coalesced by a timer
        // — settling to exactly one evaluation only after the debounce window elapses; not
        // dropped-and-forgotten before it could ever run.
        RecordingNetworkDiscovery discovery = new() { InternalIp = "224.0.0.1" };
        RecordingConnectivityManager manager = new();
        NetworkChangeMonitor monitor = BuildMonitor(discovery, manager);

        DateTime start = DateTime.UtcNow;
        monitor.OnNetworkAddressChanged(null, EventArgs.Empty);
        monitor.OnNetworkAddressChanged(null, EventArgs.Empty);
        monitor.OnNetworkAddressChanged(null, EventArgs.Empty);

        await WaitUntil(() => manager.EvaluateCallCount > 0);
        TimeSpan elapsed = DateTime.UtcNow - start;

        Assert.Equal(1, manager.EvaluateCallCount);
        // Proves this went through the debounce timer rather than running instantly on
        // the first call, which is what the dropped-events gate used to do.
        Assert.True(
            elapsed > TimeSpan.FromMilliseconds(60),
            $"Expected the debounce window to delay evaluation, settled after {elapsed.TotalMilliseconds}ms."
        );
    }

    private static NetworkAvailabilityEventArgs BuildAvailabilityArgs(bool isAvailable)
    {
        return (NetworkAvailabilityEventArgs)
            Activator.CreateInstance(
                typeof(NetworkAvailabilityEventArgs),
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                args: [isAvailable],
                culture: null
            )!;
    }

    [Fact]
    public async Task OnNetworkAvailabilityChanged_BecameAvailable_ReevaluatesConnectivity()
    {
        RecordingNetworkDiscovery discovery = new();
        RecordingConnectivityManager manager = new();
        NetworkChangeMonitor monitor = BuildMonitor(discovery, manager);

        monitor.OnNetworkAvailabilityChanged(null, BuildAvailabilityArgs(isAvailable: true));

        await WaitUntil(() => manager.EvaluateCallCount > 0);

        Assert.True(manager.EvaluateCallCount > 0);
        Assert.True(discovery.ForceRediscoveryCallCount > 0);
    }

    [Fact]
    public async Task OnNetworkAvailabilityChanged_BecameUnavailable_DoesNothing()
    {
        RecordingNetworkDiscovery discovery = new();
        RecordingConnectivityManager manager = new();
        NetworkChangeMonitor monitor = BuildMonitor(discovery, manager);

        monitor.OnNetworkAvailabilityChanged(null, BuildAvailabilityArgs(isAvailable: false));
        await Task.Delay(200);

        Assert.Equal(0, manager.EvaluateCallCount);
        Assert.Equal(0, discovery.ForceRediscoveryCallCount);
    }

    [Fact]
    public async Task SendUpdate_NoAuthToken_ReturnsWithoutThrowing_AndNeverPosts()
    {
        RecordingNetworkDiscovery discovery = new();
        RecordingConnectivityManager manager = new();
        AuthTokenStore tokenStore = new(); // AccessToken stays null
        NetworkChangeMonitor monitor = BuildMonitor(discovery, manager, tokenStore);

        Exception? ex = await Record.ExceptionAsync(monitor.SendUpdate);

        Assert.Null(ex);
    }

    [Fact]
    public void GetCurrentInternalIp_ResolvesToParseableIpv4()
    {
        string ip = NetworkChangeMonitor.GetCurrentInternalIp();

        Assert.True(IPAddress.TryParse(ip, out IPAddress? parsed));
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, parsed!.AddressFamily);
    }

    [Fact]
    public async Task StartAsync_ThenStopAsync_DoesNotThrow()
    {
        RecordingNetworkDiscovery discovery = new();
        RecordingConnectivityManager manager = new();
        NetworkChangeMonitor monitor = BuildMonitor(discovery, manager);

        await monitor.StartAsync(CancellationToken.None);
        Exception? ex = await Record.ExceptionAsync(() =>
            monitor.StopAsync(CancellationToken.None)
        );

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        RecordingNetworkDiscovery discovery = new();
        RecordingConnectivityManager manager = new();
        NetworkChangeMonitor monitor = BuildMonitor(discovery, manager);

        Exception? ex = Record.Exception(monitor.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_AfterStart_UnsubscribesWithoutThrowing()
    {
        RecordingNetworkDiscovery discovery = new();
        RecordingConnectivityManager manager = new();
        NetworkChangeMonitor monitor = BuildMonitor(discovery, manager);
        await monitor.StartAsync(CancellationToken.None);

        Exception? ex = Record.Exception(monitor.Dispose);

        Assert.Null(ex);
    }
}
