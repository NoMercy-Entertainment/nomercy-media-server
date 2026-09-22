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

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Network;
using NoMercy.PluginSdk.Runtime;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A router mapping is the one thing a plugin holds that outlives the
/// process. One the plugin never drops is a hole in the owner's router that
/// nothing closes.
/// </summary>
public class PluginPortMapTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000015");

    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static (
        PluginPortMap Map,
        FakePortMapClient Client,
        PluginResourceLedger Ledger,
        MovableClock Clock
    ) Map(bool declared)
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
        PluginResourceLedger ledger = new();
        MovableClock clock = new(Noon);

        return (new(Plugin, broker, client, ledger, clock), client, ledger, clock);
    }

    [Fact]
    public async Task Mapping_without_network_listen_refuses_and_names_the_port()
    {
        (PluginPortMap map, FakePortMapClient client, _, _) = Map(declared: false);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            map.MapAsync(6881, 6881, PluginTransport.Tcp, TimeSpan.FromMinutes(30))
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ListenerUndeclared);
        refused.Refusal.What.Should().Contain("6881");
        client.Mapped.Should().BeEmpty("the router is told nothing until the owner has agreed");
    }

    [Fact]
    public async Task A_mapping_answers_what_the_router_granted_not_what_was_asked()
    {
        (PluginPortMap map, FakePortMapClient client, _, _) = Map(declared: true);
        client.GrantExternalPort = 6900;

        PluginPortMapping mapping = await map.MapAsync(
            6881,
            6881,
            PluginTransport.Tcp,
            TimeSpan.FromMinutes(30)
        );

        mapping.InternalPort.Should().Be(6881);
        mapping
            .ExternalPort.Should()
            .Be(6900, "answering the request advertises a port nothing is forwarded to");
        mapping.ExpiresAt.Should().Be(Noon.AddMinutes(30));
    }

    [Fact]
    public async Task Listing_answers_every_mapping_the_plugin_holds()
    {
        (PluginPortMap map, _, _, _) = Map(declared: true);
        await map.MapAsync(6881, 6881, PluginTransport.Tcp, TimeSpan.FromMinutes(30));
        await map.MapAsync(6882, 6882, PluginTransport.Udp, TimeSpan.FromMinutes(30));

        IReadOnlyList<PluginPortMapping> held = await map.ListAsync();

        held.Should().HaveCount(2);
        held.Select(mapping => mapping.InternalPort).Should().BeEquivalentTo([6881, 6882]);
        held.Should().ContainSingle(mapping => mapping.Transport == PluginTransport.Udp);
    }

    [Fact]
    public async Task Unmapping_tells_the_router_and_leaves_the_list()
    {
        (PluginPortMap map, FakePortMapClient client, _, _) = Map(declared: true);
        PluginPortMapping mapping = await map.MapAsync(
            6881,
            6881,
            PluginTransport.Tcp,
            TimeSpan.FromMinutes(30)
        );

        await map.UnmapAsync(mapping);

        client.Unmapped.Should().ContainSingle().Which.ExternalPort.Should().Be(6881);
        (await map.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_lease_more_than_half_spent_is_renewed()
    {
        (PluginPortMap map, FakePortMapClient client, _, MovableClock clock) = Map(declared: true);
        await map.MapAsync(6881, 6881, PluginTransport.Tcp, TimeSpan.FromMinutes(30));

        clock.Advance(TimeSpan.FromMinutes(16));
        await map.RenewDueAsync();

        client.Mapped.Should().HaveCount(2);
        (await map.ListAsync())[0].ExpiresAt.Should().Be(Noon.AddMinutes(46));
    }

    [Fact]
    public async Task A_fresh_lease_is_not_renewed()
    {
        (PluginPortMap map, FakePortMapClient client, _, MovableClock clock) = Map(declared: true);
        await map.MapAsync(6881, 6881, PluginTransport.Tcp, TimeSpan.FromMinutes(30));

        clock.Advance(TimeSpan.FromMinutes(5));
        await map.RenewDueAsync();

        client
            .Mapped.Should()
            .ContainSingle("renewing every minute is a router asked to work for nothing");
    }

    [Fact]
    public async Task Stopping_the_plugin_drops_every_mapping_at_the_router()
    {
        (PluginPortMap map, FakePortMapClient client, PluginResourceLedger ledger, _) = Map(
            declared: true
        );
        await map.MapAsync(6881, 6881, PluginTransport.Tcp, TimeSpan.FromMinutes(30));
        await map.MapAsync(6882, 6882, PluginTransport.Tcp, TimeSpan.FromMinutes(30));

        await ledger.ReleaseAsync(Plugin);

        client.Unmapped.Should().HaveCount(2);
        client
            .Unmapped.Select(mapping => mapping.InternalPort)
            .Should()
            .BeEquivalentTo([6881, 6882]);
        (await map.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_mapping_the_plugin_dropped_is_not_dropped_again_when_it_stops()
    {
        (PluginPortMap map, FakePortMapClient client, PluginResourceLedger ledger, _) = Map(
            declared: true
        );
        PluginPortMapping mapping = await map.MapAsync(
            6881,
            6881,
            PluginTransport.Tcp,
            TimeSpan.FromMinutes(30)
        );
        await map.UnmapAsync(mapping);

        await ledger.ReleaseAsync(Plugin);

        client
            .Unmapped.Should()
            .ContainSingle("the second call names a mapping the router no longer has");
    }
}
