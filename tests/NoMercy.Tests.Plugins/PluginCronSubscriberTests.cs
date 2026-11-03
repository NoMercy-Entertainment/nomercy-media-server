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

using Moq;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk.Hooks;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin that loads after the boot pass (a late load, an install, an
/// approval, a restart) announces itself only with
/// <see cref="PluginLoadedEvent"/>. The boot pass's RegisterAll has already
/// run by then, so this subscriber is what schedules its jobs.
/// </summary>
public class PluginCronSubscriberTests
{
    private static PluginLoadedEvent Loaded(Ulid pluginId) =>
        new()
        {
            PluginId = pluginId.ToString(),
            PluginName = "Scheduled",
            Version = "1.0.0",
        };

    [Fact]
    public async Task APluginThatLoadsThroughTheEvent_GetsItsScheduledJobsRegistered()
    {
        InMemoryEventBus eventBus = new();
        Mock<IPluginCronRegistrar> registrar = new();
        PluginCronSubscriber subscriber = new(eventBus, registrar.Object);
        await subscriber.StartAsync(CancellationToken.None);
        Ulid pluginId = Ulid.NewUlid();

        await eventBus.PublishAsync(Loaded(pluginId));

        registrar.Verify(
            r => r.RegisterPlugin(pluginId),
            Times.Once,
            "a plugin loaded after boot must have its scheduled jobs registered"
        );
    }

    [Fact]
    public async Task ADisabledPlugin_HasItsScheduledJobsRemoved()
    {
        InMemoryEventBus eventBus = new();
        Mock<IPluginCronRegistrar> registrar = new();
        PluginCronSubscriber subscriber = new(eventBus, registrar.Object);
        await subscriber.StartAsync(CancellationToken.None);
        Ulid pluginId = Ulid.NewUlid();

        await eventBus.PublishAsync(
            new PluginDisabledEvent { PluginId = pluginId.ToString(), PluginName = "Scheduled" }
        );

        registrar.Verify(r => r.UnregisterPlugin(pluginId), Times.Once);
    }

    [Fact]
    public async Task AfterStop_NothingIsRegistered()
    {
        InMemoryEventBus eventBus = new();
        Mock<IPluginCronRegistrar> registrar = new();
        PluginCronSubscriber subscriber = new(eventBus, registrar.Object);
        await subscriber.StartAsync(CancellationToken.None);
        await subscriber.StopAsync(CancellationToken.None);

        await eventBus.PublishAsync(Loaded(Ulid.NewUlid()));

        registrar.Verify(r => r.RegisterPlugin(It.IsAny<Ulid>()), Times.Never);
    }

    [Fact]
    public async Task AnEventWithAnUnreadableId_IsIgnored()
    {
        InMemoryEventBus eventBus = new();
        Mock<IPluginCronRegistrar> registrar = new();
        PluginCronSubscriber subscriber = new(eventBus, registrar.Object);
        await subscriber.StartAsync(CancellationToken.None);

        await eventBus.PublishAsync(
            new PluginLoadedEvent
            {
                PluginId = "not-an-id",
                PluginName = "Scheduled",
                Version = "1.0.0",
            }
        );

        registrar.Verify(r => r.RegisterPlugin(It.IsAny<Ulid>()), Times.Never);
    }
}
