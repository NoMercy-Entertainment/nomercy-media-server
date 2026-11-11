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

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Capabilities;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

[Trait("Category", "Unit")]
public class PluginHubGroupRevocationTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000019");
    private static readonly Guid Owner = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Other = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Fact]
    public void The_existing_sender_constructor_still_sends_access_notifications()
    {
        Mock<IClientProxy> user = new();
        Mock<IHubClients> clients = new();
        clients.Setup(value => value.User(Owner.ToString())).Returns(user.Object);
        Mock<IHubContext<PluginHub>> hub = new();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        PluginAccessHubSender sender = new(hub.Object);

        sender.Send(Owner, Plugin, "none");

        user.Verify(
            value =>
                value.SendCoreAsync(
                    "PluginAccessChanged",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Access_loss_removes_every_plugin_hub_connection_for_that_user()
    {
        (_, Mock<IGroupManager> groups, InMemoryEventBus events) = Build();

        await events.PublishAsync(new PluginAccessChangedEvent { PluginId = Plugin.ToString() });

        groups.Verify(
            group =>
                group.RemoveFromGroupAsync(
                    "owner-1",
                    PluginHub.GroupFor(Plugin),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        groups.Verify(
            group =>
                group.RemoveFromGroupAsync(
                    "owner-2",
                    PluginHub.GroupFor(Plugin),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        groups.Verify(
            group =>
                group.RemoveFromGroupAsync(
                    "other",
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        groups.Verify(
            group =>
                group.RemoveFromGroupAsync(
                    "owner-other-hub",
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task A_list_change_removes_connections_for_plugins_the_user_lost()
    {
        (_, Mock<IGroupManager> groups, InMemoryEventBus events) = Build();

        await events.PublishAsync(new PluginAccessChangedEvent());

        groups.Verify(
            group =>
                group.RemoveFromGroupAsync(
                    "owner-1",
                    PluginHub.GroupFor(Plugin),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabling_or_uninstalling_a_plugin_removes_all_its_connections(
        bool uninstalled
    )
    {
        (PluginAccessHubSender _, Mock<IGroupManager> groups, InMemoryEventBus events) = Build();

        await events.PublishAsync(
            new PluginDisabledEvent
            {
                PluginId = Plugin.ToString(),
                PluginName = "Sample",
                Uninstalled = uninstalled,
            }
        );

        foreach (string connectionId in new[] { "owner-1", "owner-2", "other" })
            groups.Verify(
                group =>
                    group.RemoveFromGroupAsync(
                        connectionId,
                        PluginHub.GroupFor(Plugin),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Once
            );
        groups.Verify(
            group =>
                group.RemoveFromGroupAsync(
                    "owner-other-hub",
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    private static (
        PluginAccessHubSender Sender,
        Mock<IGroupManager> Groups,
        InMemoryEventBus Events
    ) Build()
    {
        ConnectedClients connected = new();
        connected.Clients.TryAdd("owner-1", new Client { Sub = Owner, Endpoint = "/pluginHub" });
        connected.Clients.TryAdd("owner-2", new Client { Sub = Owner, Endpoint = "/pluginHub" });
        connected.Clients.TryAdd("other", new Client { Sub = Other, Endpoint = "/pluginHub" });
        connected.Clients.TryAdd(
            "owner-other-hub",
            new Client { Sub = Owner, Endpoint = "/musicHub" }
        );

        Mock<IGroupManager> groups = new();
        Mock<IClientProxy> user = new();
        Mock<IHubClients> clients = new();
        clients.Setup(value => value.User(Owner.ToString())).Returns(user.Object);
        Mock<IHubContext<PluginHub>> hub = new();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        hub.SetupGet(value => value.Groups).Returns(groups.Object);

        InMemoryEventBus events = new();
        Mock<IPluginAccessResolver> access = new();
        access.Setup(value => value.Resolve(Plugin, Owner)).Returns(PluginAccess.None);
        access.Setup(value => value.Resolve(Plugin, Other)).Returns(PluginAccess.Shared);
        Mock<IPluginManifestSource> plugins = new();
        plugins
            .Setup(value => value.All())
            .Returns([
                new PluginInfo
                {
                    Id = Plugin,
                    Name = "Sample",
                    Description = "d",
                    Version = new(1, 0, 0),
                    Status = PluginStatus.Active,
                },
            ]);
        ServiceCollection services = new();
        services.AddSingleton(hub.Object);
        services.AddSingleton(connected);
        services.AddSingleton<IEventBus>(events);
        services.AddSingleton(access.Object);
        services.AddSingleton(plugins.Object);
        services.AddSingleton<PluginAccessHubSender>();
        ServiceProvider provider = services.BuildServiceProvider();

        return (provider.GetRequiredService<PluginAccessHubSender>(), groups, events);
    }
}
