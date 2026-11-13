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
using NoMercy.Api.EventHandlers;
using NoMercy.Authorization;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Media;
using NoMercy.Events.Plugins;
using Xunit;

namespace NoMercy.Tests.Api.EventHandlers;

public sealed class PluginConsentNotificationTests
{
    [Fact]
    public async Task The_consent_notice_opens_the_plugins_own_page()
    {
        InMemoryEventBus bus = new();
        Mock<IUserCache> users = new();
        users
            .SetupGet(cache => cache.Users)
            .Returns([new User { Id = Guid.NewGuid(), Owner = true }]);

        List<UserNotifiedEvent> notices = [];
        using IDisposable listening = bus.Subscribe<UserNotifiedEvent>(
            (notice, _) =>
            {
                notices.Add(notice);
                return Task.CompletedTask;
            }
        );
        using PluginConsentEventHandler handler = new(bus, users.Object);

        string pluginId = Ulid.NewUlid().ToString();
        await bus.PublishAsync(
            new PluginConsentRequiredEvent
            {
                PluginId = pluginId,
                PluginName = "Example plugin",
                Reason = "capabilities-widened",
            }
        );

        UserNotifiedEvent sent = Assert.Single(notices);
        Assert.Equal($"/dashboard/plugins/manage/{pluginId}", sent.Route);
    }
}
