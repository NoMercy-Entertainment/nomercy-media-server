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
/// Discovery enumerates machines the owner never mentioned to the server:
/// what is in their house and when it is switched on. So it is its own
/// capability with its own list of service types, and the refusal lands
/// before anything reaches the network.
/// </summary>
public class PluginNetDiscoveryTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000014");

    private static (
        PluginNetDiscovery Discovery,
        FakeDiscoveryClient Client,
        PluginResourceLedger Ledger
    ) Discovery(string? declaredProtocol)
    {
        PluginCapabilities? capabilities = declaredProtocol is null
            ? null
            : new()
            {
                Hooks = [PluginCapabilityNames.NetworkDiscover],
                Network = new() { Protocols = [declaredProtocol] },
            };

        NetworkPlugin plugin = new(Plugin, capabilities);
        PluginCapabilityBroker broker = new(
            plugin,
            plugin,
            plugin,
            new PluginRefusalCounter(),
            NullLogger<PluginCapabilityBroker>.Instance
        );
        FakeDiscoveryClient client = new();
        PluginResourceLedger ledger = new();

        return (new(Plugin, broker, client, ledger), client, ledger);
    }

    [Fact]
    public async Task Browsing_without_the_capability_refuses_before_it_touches_the_network()
    {
        (PluginNetDiscovery discovery, FakeDiscoveryClient client, _) = Discovery(null);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(
            async () =>
            {
                await foreach (
                    PluginDiscoveredService _ in discovery.BrowseAsync("_bittorrent._tcp")
                )
                    break;
            }
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
        refused.Refusal.Fix.Should().Contain(PluginCapabilityNames.NetworkDiscover);
        client
            .Browsed.Should()
            .BeEmpty("a refusal after the first multicast has already leaked what it wanted");
    }

    [Fact]
    public async Task Browsing_a_service_type_outside_the_declaration_refuses()
    {
        (PluginNetDiscovery discovery, FakeDiscoveryClient client, _) = Discovery(
            "_bittorrent._tcp"
        );

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(
            async () =>
            {
                await foreach (PluginDiscoveredService _ in discovery.BrowseAsync("_airplay._tcp"))
                    break;
            }
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.CapabilityScopeRefused);
        refused.Refusal.What.Should().Contain("_airplay._tcp");
        client.Browsed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_glob_does_not_let_a_plugin_enumerate_the_network()
    {
        NetworkPlugin plugin = new(
            Plugin,
            new()
            {
                Hooks = [PluginCapabilityNames.NetworkDiscover],
                Network = new() { Hosts = ["_bittorrent._tcp", "*"] },
            }
        );
        PluginCapabilityBroker broker = new(
            plugin,
            plugin,
            plugin,
            new PluginRefusalCounter(),
            NullLogger<PluginCapabilityBroker>.Instance
        );
        PluginNetDiscovery discovery = new(
            Plugin,
            broker,
            new FakeDiscoveryClient(),
            new PluginResourceLedger()
        );

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(
            async () =>
            {
                await foreach (
                    PluginDiscoveredService _ in discovery.BrowseAsync("_bittorrent._tcp")
                )
                    break;
            }
        );

        refused
            .Refusal.Code.Should()
            .Be(
                PluginRefusalCodes.CapabilityScopeRefused,
                "a service type is not a host, and reading one list for both would let a plugin allowed to fetch one site inventory a home"
            );
    }

    [Fact]
    public async Task Browsing_answers_what_the_network_answered()
    {
        (PluginNetDiscovery discovery, FakeDiscoveryClient client, _) = Discovery(
            "_bittorrent._tcp"
        );
        client.Answers.Add(
            new("_bittorrent._tcp", "nas", "192.168.2.120", 6881, new Dictionary<string, string>())
        );

        List<PluginDiscoveredService> found = [];

        await foreach (PluginDiscoveredService service in discovery.BrowseAsync("_bittorrent._tcp"))
            found.Add(service);

        found.Should().ContainSingle();
        found[0].Instance.Should().Be("nas");
        found[0].Host.Should().Be("192.168.2.120");
        found[0].Port.Should().Be(6881);
        client.Browsed.Should().ContainSingle().Which.Should().Be("_bittorrent._tcp");
    }

    [Fact]
    public async Task Announcing_without_the_capability_refuses()
    {
        (PluginNetDiscovery discovery, FakeDiscoveryClient client, _) = Discovery(null);

        await Assert.ThrowsAsync<PluginRefusedException>(() =>
            discovery.AnnounceAsync("_bittorrent._tcp", "nomercy", 6881)
        );

        client.Announced.Should().BeEmpty();
    }

    [Fact]
    public async Task An_announcement_is_live_until_its_handle_is_disposed()
    {
        (PluginNetDiscovery discovery, FakeDiscoveryClient client, _) = Discovery(
            "_bittorrent._tcp"
        );

        IAsyncDisposable handle = await discovery.AnnounceAsync(
            "_bittorrent._tcp",
            "nomercy",
            6881,
            new Dictionary<string, string> { ["id"] = "abc" }
        );

        client.Announced.Should().ContainSingle();
        client.Stopped.Should().BeEmpty();

        await handle.DisposeAsync();

        client.Stopped.Should().ContainSingle().Which.Should().Be("nomercy");
    }

    [Fact]
    public async Task Stopping_the_plugin_stops_an_announcement_it_never_disposed()
    {
        (PluginNetDiscovery discovery, FakeDiscoveryClient client, PluginResourceLedger ledger) =
            Discovery("_bittorrent._tcp");

        await discovery.AnnounceAsync("_bittorrent._tcp", "nomercy", 6881);
        ledger.Held(Plugin).Should().Be(1);

        await ledger.ReleaseAsync(Plugin);

        client.Stopped.Should().ContainSingle().Which.Should().Be("nomercy");
    }

    [Fact]
    public async Task An_announcement_the_plugin_stopped_is_not_stopped_twice()
    {
        (PluginNetDiscovery discovery, FakeDiscoveryClient client, PluginResourceLedger ledger) =
            Discovery("_bittorrent._tcp");

        IAsyncDisposable handle = await discovery.AnnounceAsync(
            "_bittorrent._tcp",
            "nomercy",
            6881
        );
        await handle.DisposeAsync();

        ledger
            .Held(Plugin)
            .Should()
            .Be(0, "a plugin that tidied up should not still be listed as holding it");

        await ledger.ReleaseAsync(Plugin);

        client
            .Stopped.Should()
            .ContainSingle("a goodbye sent twice names an instance that is already gone");
    }
}
