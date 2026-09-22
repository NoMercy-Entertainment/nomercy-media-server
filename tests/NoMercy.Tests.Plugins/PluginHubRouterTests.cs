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

using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Hub;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// One hub carries every plugin's traffic, so the router is the only thing
/// standing between a client naming a plugin id and that plugin's code running.
/// It is also the only place a disabled plugin stops receiving without anything
/// being unmapped.
/// </summary>
[Trait("Category", "Unit")]
public class PluginHubRouterTests
{
    private static readonly Ulid PluginId = Ulid.Parse("5ANANANEXVSK6DVQFEXVQEXVQE");

    private sealed class RecordingHandler(Ulid pluginId) : IPluginHubHandler
    {
        public Ulid PluginId { get; } = pluginId;
        public List<string> Received { get; } = [];
        public bool Throws { get; init; }

        public Task HandleAsync(
            PluginHubMessage message,
            IPluginHubClient client,
            CancellationToken ct
        )
        {
            if (Throws)
                throw new InvalidOperationException("plugin blew up");

            Received.Add(message.Method);
            return Task.CompletedTask;
        }
    }

    private sealed class SilentClient : IPluginHubClient
    {
        public Task SendAsync(string type, object? payload) => Task.CompletedTask;
    }

    private sealed class OnePluginManager(PluginInfo? info) : IPluginManager
    {
        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => info is null ? [] : [info];

        public PluginInfo? GetPluginInfo(Ulid pluginId) =>
            info is not null && info.Id == pluginId ? info : null;

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);

        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => [];
    }

    private static PluginInfo Plugin(PluginStatus status, bool ws) =>
        new()
        {
            Id = PluginId,
            Name = "n",
            Description = "d",
            Version = new(1, 0, 0),
            Status = status,
            Capabilities = new() { Ws = ws },
        };

    private static PluginHubRouter Router(PluginInfo? info) =>
        new(new OnePluginManager(info), NullLogger<PluginHubRouter>.Instance);

    private static PluginHubMessage Message(string method = "ping") =>
        new()
        {
            Method = method,
            Payload = JsonNode.Parse("""{"n":1}"""),
            ConnectionId = "conn-1",
        };

    [Fact]
    public async Task An_active_plugin_that_declared_ws_receives_what_a_client_sends()
    {
        PluginHubRouter router = Router(Plugin(PluginStatus.Active, ws: true));
        RecordingHandler handler = new(PluginId);
        router.Register(handler);

        bool routed = await router.RouteAsync(
            PluginId,
            Message(),
            new SilentClient(),
            CancellationToken.None
        );

        routed.Should().BeTrue();
        handler.Received.Should().ContainSingle().Which.Should().Be("ping");
    }

    [Fact]
    public async Task A_plugin_that_never_declared_ws_gets_nothing()
    {
        // Declaring is asking the owner. A live channel a plugin never asked
        // for is exactly the capability model going unenforced.
        PluginHubRouter router = Router(Plugin(PluginStatus.Active, ws: false));
        RecordingHandler handler = new(PluginId);
        router.Register(handler);

        bool routed = await router.RouteAsync(
            PluginId,
            Message(),
            new SilentClient(),
            CancellationToken.None
        );

        routed.Should().BeFalse();
        handler.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disabled_plugin_stops_receiving_without_anything_being_unmapped()
    {
        PluginHubRouter router = Router(Plugin(PluginStatus.Disabled, ws: true));
        RecordingHandler handler = new(PluginId);
        router.Register(handler);

        bool routed = await router.RouteAsync(
            PluginId,
            Message(),
            new SilentClient(),
            CancellationToken.None
        );

        routed.Should().BeFalse();
        handler.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task An_id_with_no_handler_is_dropped_rather_than_throwing()
    {
        PluginHubRouter router = Router(Plugin(PluginStatus.Active, ws: true));

        bool routed = await router.RouteAsync(
            PluginId,
            Message(),
            new SilentClient(),
            CancellationToken.None
        );

        routed.Should().BeFalse();
    }

    [Fact]
    public async Task An_unregistered_plugin_is_dropped_after_teardown()
    {
        PluginHubRouter router = Router(Plugin(PluginStatus.Active, ws: true));
        router.Register(new RecordingHandler(PluginId));
        router.Unregister(PluginId);

        bool routed = await router.RouteAsync(
            PluginId,
            Message(),
            new SilentClient(),
            CancellationToken.None
        );

        routed.Should().BeFalse();
    }

    [Fact]
    public async Task A_throwing_plugin_does_not_take_the_shared_connection_down()
    {
        // Every plugin is multiplexed over one connection. An exception escaping
        // here would drop the hub for all of them.
        PluginHubRouter router = Router(Plugin(PluginStatus.Active, ws: true));
        router.Register(new RecordingHandler(PluginId) { Throws = true });

        Func<Task<bool>> route = () =>
            router.RouteAsync(PluginId, Message(), new SilentClient(), CancellationToken.None);

        (await route.Should().NotThrowAsync()).Which.Should().BeFalse();
    }

    [Fact]
    public async Task A_plugin_the_manager_never_heard_of_gets_nothing()
    {
        PluginHubRouter router = Router(info: null);
        router.Register(new RecordingHandler(PluginId));

        bool routed = await router.RouteAsync(
            PluginId,
            Message(),
            new SilentClient(),
            CancellationToken.None
        );

        routed.Should().BeFalse();
    }
}
