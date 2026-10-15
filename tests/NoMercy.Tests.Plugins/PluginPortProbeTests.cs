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
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Network;
using NoMercy.PluginSdk.Runtime;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A torrent client tells the owner whether its port is reachable from
/// outside, and warns only when it is truly shut. The server tests that with a
/// real connection, and only for ports the plugin may use.
/// </summary>
[Trait("Category", "Unit")]
public class PluginPortProbeTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000018");

    private static (PluginPortMap Map, FakePortMapClient Client) Map(bool declared)
    {
        PluginCapabilities? capabilities = declared
            ? new()
            {
                Hooks = [PluginCapabilityNames.NetworkListen],
                Network = new() { Ports = ["6881-6889"] },
            }
            : null;

        NetworkPlugin plugin = new(Plugin, capabilities);
        PluginCapabilityBroker broker = new(
            plugin,
            plugin,
            plugin,
            new PluginRefusalCounter(),
            NullLogger<PluginCapabilityBroker>.Instance
        );
        FakePortMapClient client = new();

        return (
            new(Plugin, broker, client, new PluginResourceLedger(), TimeProvider.System, plugin),
            client
        );
    }

    [Theory]
    [InlineData(PluginPortReachability.Open)]
    [InlineData(PluginPortReachability.Closed)]
    public async Task A_declared_port_is_probed_and_the_answer_comes_back(
        PluginPortReachability answer
    )
    {
        (PluginPortMap map, FakePortMapClient client) = Map(declared: true);
        client.ProbeAnswer = answer;

        PluginPortReachability found = await map.ProbeAsync(6881, PluginTransport.Tcp);

        found.Should().Be(answer);
        client.Probed.Should().Equal(6881);
    }

    [Fact]
    public async Task Without_network_listen_the_probe_is_refused_and_nothing_is_asked()
    {
        (PluginPortMap map, FakePortMapClient client) = Map(declared: false);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            map.ProbeAsync(6881, PluginTransport.Tcp)
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ListenerUndeclared);
        client.Probed.Should().BeEmpty();
    }

    /// <summary>Probing any port would let a plugin map the owner's router from outside.</summary>
    [Fact]
    public async Task A_port_the_plugin_neither_declared_nor_mapped_is_refused()
    {
        (PluginPortMap map, FakePortMapClient client) = Map(declared: true);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            map.ProbeAsync(3389, PluginTransport.Tcp)
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ListenerUndeclared);
        refused.Refusal.What.Should().Contain("3389");
        client.Probed.Should().BeEmpty();
    }

    /// <summary>
    /// A router grants the external port it chooses. The port it granted is
    /// one the plugin holds, so it may ask about it, inside its range or not.
    /// </summary>
    [Fact]
    public async Task The_external_port_a_router_granted_may_be_probed()
    {
        (PluginPortMap map, FakePortMapClient client) = Map(declared: true);
        client.GrantExternalPort = 40_123;
        await map.MapAsync(6881, 6881, PluginTransport.Tcp, TimeSpan.FromMinutes(30));

        PluginPortReachability found = await map.ProbeAsync(40_123, PluginTransport.Tcp);

        found.Should().Be(PluginPortReachability.Open);
        client.Probed.Should().Equal(40_123);
    }

    /// <summary>A UDP port answers nothing unless the program behind it chooses to.</summary>
    [Fact]
    public async Task A_udp_port_is_unknown_and_not_probed()
    {
        (PluginPortMap map, FakePortMapClient client) = Map(declared: true);

        (await map.ProbeAsync(6881, PluginTransport.Udp))
            .Should()
            .Be(PluginPortReachability.Unknown);
        client.Probed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_port_map_written_before_the_probe_answers_unknown()
    {
        IPluginPortMap older = new OlderPortMap();

        (await older.ProbeAsync(6881, PluginTransport.Tcp))
            .Should()
            .Be(PluginPortReachability.Unknown);
    }

    [Fact]
    public async Task A_port_with_a_listener_reads_as_open()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            PluginPortReachability found = await NatPortMapClient.ConnectAsync(
                IPAddress.Loopback,
                port,
                TimeSpan.FromSeconds(3),
                default
            );

            found.Should().Be(PluginPortReachability.Open);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_port_nobody_listens_on_reads_as_closed()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        PluginPortReachability found = await NatPortMapClient.ConnectAsync(
            IPAddress.Loopback,
            port,
            TimeSpan.FromSeconds(3),
            default
        );

        found.Should().Be(PluginPortReachability.Closed);
    }

    /// <summary>NAT-PMP opcode 0: version, opcode 128, result 0, epoch, then the address.</summary>
    [Fact]
    public void The_routers_public_address_is_read_from_its_answer()
    {
        byte[] answer = [0, 128, 0, 0, 0, 0, 1, 0, 203, 0, 113, 7];

        NatPortMapClient.ParseExternalAddress(answer).Should().Be(IPAddress.Parse("203.0.113.7"));
    }

    [Theory]
    [InlineData(new byte[] { 0, 128, 0, 3, 0, 0, 1, 0, 203, 0, 113, 7 })]
    [InlineData(new byte[] { 0, 129, 0, 0, 0, 0, 1, 0, 203, 0, 113, 7 })]
    [InlineData(new byte[] { 0, 128, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 128, 0, 0 })]
    public void An_answer_that_is_not_an_address_reads_as_none(byte[] answer)
    {
        NatPortMapClient.ParseExternalAddress(answer).Should().BeNull();
    }

    private sealed class OlderPortMap : IPluginPortMap
    {
        public Task<PluginPortMapping> MapAsync(
            int internalPort,
            int externalPort,
            PluginTransport transport,
            TimeSpan lease,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task UnmapAsync(PluginPortMapping mapping, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PluginPortMapping>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginPortMapping>>([]);
    }
}
