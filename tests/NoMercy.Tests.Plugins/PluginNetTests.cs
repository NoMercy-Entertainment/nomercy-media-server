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
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Network;
using NoMercy.PluginSdk.Runtime;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Three things can be wrong with a socket, and each has a different fix: the
/// capability was never declared or approved, the host is outside the globs,
/// or the port is outside the range. One message for all three sends an author
/// to edit a manifest line that was already right.
/// </summary>
public class PluginNetTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000013");

    private static (PluginNet Net, PluginResourceLedger Ledger) Net(
        string? dialHost,
        string? listenPorts,
        bool declareListen = true,
        Func<TcpClient>? tcp = null,
        Func<UdpClient>? udp = null
    )
    {
        List<string> hooks = [];

        if (dialHost is not null)
            hooks.Add(PluginCapabilityNames.NetworkDial);

        if (listenPorts is not null && declareListen)
            hooks.Add(PluginCapabilityNames.NetworkListen);

        PluginCapabilities capabilities = new()
        {
            Hooks = hooks,
            Network = new()
            {
                Hosts = dialHost is null ? [] : [dialHost],
                Ports = listenPorts is null ? [] : [listenPorts],
            },
        };

        StubManifests manifests = new(
            Plugin,
            hooks.Count == 0 && listenPorts is null ? null : capabilities
        );
        PluginCapabilityBroker broker = new(
            manifests,
            new AlwaysConsented(),
            new NothingGranted(),
            new PluginRefusalCounter(),
            NullLogger<PluginCapabilityBroker>.Instance
        );
        PluginResourceLedger ledger = new();

        // Loopback, never the wildcard: a wildcard listener asks the Windows
        // firewall on every test run.
        PluginNet net = new(Plugin, broker, manifests, ledger, bindAddress: IPAddress.Loopback)
        {
            NewTcpClient = tcp ?? (() => new TcpClient()),
            NewUdpClient = udp ?? (() => new UdpClient()),
        };

        return (net, ledger);
    }

    private static async Task<int> EchoServerAsync(CancellationToken ct)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(
            async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(ct);
                await using NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[4];
                int read = await stream.ReadAsync(buffer, ct);
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                listener.Stop();
            },
            ct
        );

        await Task.Yield();

        return port;
    }

    [Fact]
    public async Task Dialling_without_the_capability_names_the_host_and_the_port()
    {
        (PluginNet net, PluginResourceLedger _) = Net(dialHost: null, listenPorts: null);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            net.DialAsync("eztv.re", 6969, PluginTransport.Tcp)
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.SocketUndeclared);
        refused.Refusal.What.Should().Contain("eztv.re").And.Contain("6969");
        refused.Refusal.Fix.Should().Contain("network.dial");
    }

    [Fact]
    public async Task Dialling_a_host_outside_the_globs_says_so_rather_than_undeclared()
    {
        (PluginNet net, PluginResourceLedger _) = Net("*.example.com", null);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            net.DialAsync("eztv.re", 6969, PluginTransport.Tcp)
        );

        refused
            .Refusal.Code.Should()
            .Be(
                PluginRefusalCodes.HostNotAllowed,
                "the capability is there, so telling an author to declare it wastes their afternoon"
            );
        refused.Refusal.What.Should().Contain("eztv.re");
    }

    [Fact]
    public async Task Dialling_an_allowed_host_carries_bytes_both_ways()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        int port = await EchoServerAsync(cts.Token);
        (PluginNet net, PluginResourceLedger _) = Net("127.0.0.1", null);

        await using Stream stream = await net.DialAsync(
            "127.0.0.1",
            port,
            PluginTransport.Tcp,
            cts.Token
        );

        await stream.WriteAsync("ping"u8.ToArray(), cts.Token);
        byte[] back = new byte[4];
        int read = await stream.ReadAsync(back, cts.Token);

        read.Should().Be(4);
        Encoding.UTF8.GetString(back).Should().Be("ping");
    }

    [Fact]
    public async Task Listening_without_the_capability_names_the_port()
    {
        // The ports ARE declared and only the capability is missing, so the
        // range check cannot answer this one: the refusal has to come from the
        // capability check or it does not exist.
        (PluginNet net, PluginResourceLedger _) = Net(null, "6881", declareListen: false);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            net.ListenAsync(6881, PluginTransport.Tcp)
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ListenerUndeclared);
        refused.Refusal.What.Should().Contain("6881");
    }

    [Fact]
    public async Task Listening_outside_the_declared_range_refuses()
    {
        (PluginNet net, PluginResourceLedger _) = Net(null, "6881-6889");

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            net.ListenAsync(8080, PluginTransport.Tcp)
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ListenerUndeclared);
        refused.Refusal.What.Should().Contain("8080");
    }

    [Fact]
    public void A_listener_binds_the_address_the_host_was_built_with()
    {
        // The test host binds loopback so a test run never asks the firewall;
        // the server keeps the wildcard so a plugin can serve the LAN.
        (PluginNet net, PluginResourceLedger _) = Net(null, "1024-65535");

        net.BindAddress.Should().Be(IPAddress.Loopback);
    }

    [Fact]
    public async Task Listening_inside_the_declared_range_reports_the_port_it_got()
    {
        (PluginNet net, PluginResourceLedger _) = Net(null, "1024-65535");

        await using IPluginListener listener = await net.ListenAsync(0, PluginTransport.Tcp);

        listener.Port.Should().BeGreaterThan(1023, "port zero asked the host to pick one");
        listener.Port.Should().BeLessThan(65536);

        // Read from the socket, not echoed back from the request: a second
        // bind of the same port fails while this listener holds it, which a
        // made-up number would not.
        TcpListener same = new(IPAddress.Loopback, listener.Port);
        Action rebind = () => same.Start();

        rebind
            .Should()
            .Throw<SocketException>("the number answered is the port the socket actually holds");
    }

    [Fact]
    public async Task A_listener_accepts_what_the_same_plugin_dialled_into_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        (PluginNet net, PluginResourceLedger _) = Net("127.0.0.1", "1024-65535");

        await using IPluginListener listener = await net.ListenAsync(
            0,
            PluginTransport.Tcp,
            cts.Token
        );
        Task<Stream> accepted = listener.AcceptAsync(cts.Token);

        await using Stream dialled = await net.DialAsync(
            "127.0.0.1",
            listener.Port,
            PluginTransport.Tcp,
            cts.Token
        );
        await dialled.WriteAsync("pong"u8.ToArray(), cts.Token);

        await using Stream inbound = await accepted;
        byte[] buffer = new byte[4];
        int read = await inbound.ReadAsync(buffer, cts.Token);

        Encoding.UTF8.GetString(buffer, 0, read).Should().Be("pong");
    }

    [Fact]
    public async Task Stopping_the_plugin_closes_every_listener_it_holds()
    {
        (PluginNet net, PluginResourceLedger ledger) = Net(null, "1024-65535");
        IPluginListener listener = await net.ListenAsync(0, PluginTransport.Tcp);
        int port = listener.Port;
        ledger.Held(Plugin).Should().Be(1);

        await ledger.ReleaseAsync(Plugin);

        ledger.Held(Plugin).Should().Be(0);

        TcpListener rebound = new(IPAddress.Loopback, port);
        Action bind = () => rebound.Start();

        bind.Should().NotThrow("a port a stopped plugin still holds needs a server restart");
        rebound.Stop();
    }

    [Fact]
    public async Task Releasing_disposes_what_it_held_rather_than_only_forgetting_it()
    {
        PluginResourceLedger ledger = new();
        RecordingResource resource = new();
        ledger.Track(Plugin, resource);

        await ledger.ReleaseAsync(Plugin);

        resource
            .Disposed.Should()
            .BeTrue("a ledger that forgets without disposing leaves the port bound");
        ledger.Held(Plugin).Should().Be(0);
    }

    [Fact]
    public async Task Forgetting_one_resource_leaves_the_others_held()
    {
        PluginResourceLedger ledger = new();
        RecordingResource kept = new();
        RecordingResource dropped = new();
        ledger.Track(Plugin, kept);
        ledger.Track(Plugin, dropped);

        ledger.Forget(Plugin, dropped);

        ledger.Held(Plugin).Should().Be(1);

        await ledger.ReleaseAsync(Plugin);

        kept.Disposed.Should().BeTrue();
        dropped.Disposed.Should().BeFalse("it was handed back before the release");
    }

    [Fact]
    public async Task One_plugins_release_does_not_touch_another_plugins_resources()
    {
        PluginResourceLedger ledger = new();
        Ulid other = Ulid.Parse("01J9ZK5V8Y0000000000000099");
        RecordingResource mine = new();
        RecordingResource theirs = new();
        ledger.Track(Plugin, mine);
        ledger.Track(other, theirs);

        await ledger.ReleaseAsync(Plugin);

        mine.Disposed.Should().BeTrue();
        theirs.Disposed.Should().BeFalse();
        ledger.Held(other).Should().Be(1);
    }

    [Fact]
    public async Task Discovery_and_the_router_refuse_by_name_until_they_are_built()
    {
        (PluginNet net, PluginResourceLedger _) = Net("127.0.0.1", "1024-65535");

        await Task.Yield();

        Action discovery = () => _ = net.Discovery;
        Action portMap = () => _ = net.PortMap;

        discovery
            .Should()
            .Throw<PluginRefusedException>()
            .Which.Refusal.What.Should()
            .Contain("IPluginNet.Discovery");
        portMap
            .Should()
            .Throw<PluginRefusedException>()
            .Which.Refusal.What.Should()
            .Contain("IPluginNet.PortMap");
    }

    [Theory]
    [InlineData("6881", 6881, true)]
    [InlineData("6881", 6882, false)]
    [InlineData("6881-6889", 6885, true)]
    [InlineData("6881-6889", 6890, false)]
    [InlineData("6881,8080", 8080, true)]
    [InlineData(null, 6881, false)]
    [InlineData("", 6881, false)]
    public void A_port_range_is_read_the_way_the_manifest_writes_it(
        string? scope,
        int port,
        bool expected
    )
    {
        PluginPortRange.Contains(scope, port).Should().Be(expected);
    }

    /// <summary>
    /// Port zero asks the host to pick, inside the declared range. The system
    /// picks from its ephemeral ports, which a narrow range below them never
    /// contains, so the host has to find one in the range itself.
    /// </summary>
    [Theory]
    [InlineData(PluginTransport.Tcp)]
    [InlineData(PluginTransport.Udp)]
    public async Task Port_zero_gets_a_free_port_inside_a_narrow_declared_range(
        PluginTransport transport
    )
    {
        (PluginNet net, PluginResourceLedger _) = Net(null, "20100-20120");

        await using IPluginListener listener = await net.ListenAsync(0, transport);

        listener.Port.Should().BeInRange(20100, 20120);
    }

    /// <summary>Two asks get two different ports, both in the range.</summary>
    [Fact]
    public async Task Port_zero_skips_a_port_that_is_already_taken()
    {
        (PluginNet net, PluginResourceLedger _) = Net(null, "20130-20140");

        await using IPluginListener first = await net.ListenAsync(0, PluginTransport.Tcp);
        await using IPluginListener second = await net.ListenAsync(0, PluginTransport.Tcp);

        second.Port.Should().NotBe(first.Port);
        second.Port.Should().BeInRange(20130, 20140);
    }

    [Fact]
    public async Task Port_zero_with_every_declared_port_taken_says_so()
    {
        (PluginNet net, PluginResourceLedger _) = Net(null, "20150-20151");
        await using IPluginListener a = await net.ListenAsync(0, PluginTransport.Tcp);
        await using IPluginListener b = await net.ListenAsync(0, PluginTransport.Tcp);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            net.ListenAsync(0, PluginTransport.Tcp)
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ListenerUndeclared);
        refused.Refusal.Why.Should().Contain("20150-20151").And.Contain("already in use");
    }

    [Theory]
    [InlineData(new[] { "6881-6883,6885" }, new[] { 6881, 6882, 6883, 6885 })]
    [InlineData(new[] { "6881", "6881-6882" }, new[] { 6881, 6882 })]
    [InlineData(new[] { "nonsense", "", "70000" }, new int[0])]
    [InlineData(new[] { "65534-70000" }, new[] { 65534, 65535 })]
    public void The_declared_ports_are_listed_in_order_each_once(string[] declared, int[] expected)
    {
        PluginPortRange.Ports(declared).Should().Equal(expected);
    }

    /// <summary>
    /// A connect that fails gives its socket back at once. Left to the
    /// finalizer, a plugin retrying a closed port in a loop held one socket
    /// per attempt.
    /// </summary>
    [Fact]
    public async Task A_tcp_dial_that_fails_disposes_its_client()
    {
        TcpListener closed = new(IPAddress.Loopback, 0);
        closed.Start();
        int port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        TrackedTcpClient client = new();
        (PluginNet tracked, PluginResourceLedger _) = Net("127.0.0.1", null, tcp: () => client);

        await Assert.ThrowsAnyAsync<SocketException>(() =>
            tracked.DialAsync("127.0.0.1", port, PluginTransport.Tcp)
        );

        client.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task A_udp_dial_that_fails_disposes_its_client()
    {
        TrackedUdpClient client = new();
        (PluginNet tracked, PluginResourceLedger _) = Net(
            "nowhere.invalid",
            null,
            udp: () => client
        );

        await Assert.ThrowsAnyAsync<SocketException>(() =>
            tracked.DialAsync("nowhere.invalid", 9, PluginTransport.Udp)
        );

        client.Disposed.Should().BeTrue();
    }

    private sealed class TrackedTcpClient : TcpClient
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackedUdpClient : UdpClient
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingResource : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubManifests(Ulid id, PluginCapabilities? capabilities)
        : IPluginManifestSource
    {
        public PluginInfo? Find(Ulid pluginId) =>
            pluginId != id
                ? null
                : new()
                {
                    Id = id,
                    Name = "Sample",
                    Description = "d",
                    Version = new(1, 0, 0),
                    Status = PluginStatus.Active,
                    Capabilities = capabilities,
                };

        public IReadOnlyList<PluginInfo> All() => Find(id) is { } only ? [only] : [];
    }

    private sealed class AlwaysConsented : IPluginConsentService
    {
        public bool IsBaseline(PluginCapabilities? capabilities) => false;

        public bool HasConsent(Ulid pluginId) => true;

        public bool ConsentCoversCapabilities(
            Ulid pluginId,
            PluginCapabilities? capabilities,
            Version installedVersion
        ) => true;

        public PluginCapabilities? ConsentedCapabilities(Ulid pluginId) => null;

        public void ApproveCapability(Ulid pluginId, string capability, Version manifestVersion) { }

        public void RevokeCapability(Ulid pluginId, string capability) { }

        public bool IsApproved(Ulid pluginId, string capability) => true;

        public Version? ApprovedAt(Ulid pluginId, string capability) => new(1, 0, 0);

        public void GrantConsent(
            Ulid pluginId,
            PluginCapabilities? capabilities,
            Version installedVersion
        ) { }

        public void RevokeConsent(Ulid pluginId) { }
    }

    private sealed class NothingGranted : IPluginGrantStore
    {
        public IReadOnlyList<string> Granted(Ulid pluginId, string kind) => [];

        public bool Holds(Ulid pluginId, string kind, string value) => false;

        public void Grant(Ulid pluginId, string kind, string value) { }

        public void Revoke(Ulid pluginId, string kind, string value) { }

        public void RevokeAll(Ulid pluginId) { }

        public void Request(Ulid pluginId, string kind, string value, string reason) { }

        public IReadOnlyList<PluginGrantRequest> PendingRequests() => [];

        public void ClearRequest(Ulid pluginId, string kind, string value) { }
    }
}
