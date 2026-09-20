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

using NoMercy.Authorization;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Media;
using NoMercy.Events.Plugins;

namespace NoMercy.Api.EventHandlers;

/// <summary>
/// Turns <see cref="PluginConsentRequiredEvent"/> into a real notice the
/// owner sees, over the same <see cref="UserNotifiedEvent"/> pipeline every
/// other per-user notice already uses.
/// </summary>
public class PluginConsentEventHandler : EventSubscriber
{
    private readonly IEventBus _eventBus;
    private readonly IUserCache _userCache;

    public PluginConsentEventHandler(IEventBus eventBus, IUserCache userCache)
    {
        _eventBus = eventBus;
        _userCache = userCache;
        Track(eventBus.Subscribe<PluginConsentRequiredEvent>(OnConsentRequired));
    }

    internal async Task OnConsentRequired(PluginConsentRequiredEvent @event, CancellationToken ct)
    {
        User? owner = _userCache.Users.FirstOrDefault(u => u.Owner);
        if (owner is null)
            return;

        await _eventBus.PublishAsync(
            new UserNotifiedEvent
            {
                Title = "Plugin needs your approval",
                Message = $"{@event.PluginName} now asks for more access than you approved. Review it in the dashboard.",
                Type = "warning",
                UserId = owner.Id,
                Route = "/dashboard/plugins",
            },
            ct
        );
    }
}
