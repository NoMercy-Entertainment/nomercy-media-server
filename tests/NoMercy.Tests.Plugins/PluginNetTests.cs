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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Network;
using NoMercy.Plugins.Runtime;
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
        bool declareListen = true
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

        return (new(Plugin, broker, manifests, ledger), ledger);
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
    public async Task Listening_inside_the_declared_range_reports_the_port_it_got()
    {
        (PluginNet net, PluginResourceLedger _) = Net(null, "1024-65535");

        await using IPluginListener listener = await net.ListenAsync(0, PluginTransport.Tcp);

        listener.Port.Should().BeGreaterThan(1023, "port zero asked the host to pick one");
        listener.Port.Should().BeLessThan(65536);

        // Read from the socket, not echoed back from the request: a second
        // bind of the same port fails while this listener holds it, which a
        // made-up number would not.
        TcpListener same = new(IPAddress.Any, listener.Port);
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
