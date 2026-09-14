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

using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Events.Users;
using NoMercy.Networking.Messaging;

namespace NoMercy.Api.EventHandlers;

public class UserPermissionsEventHandler : EventSubscriber
{
    private readonly IClientMessenger _clientMessenger;

    private readonly ILogger<UserPermissionsEventHandler> _logger;

    public UserPermissionsEventHandler(
        ILogger<UserPermissionsEventHandler> logger,
        IEventBus eventBus,
        IClientMessenger clientMessenger
    )
    {
        _logger = logger;
        _clientMessenger = clientMessenger;
        Track(eventBus.Subscribe<UserPermissionsChangedEvent>(OnUserPermissionsChanged));
    }

    internal async Task OnUserPermissionsChanged(
        UserPermissionsChangedEvent @event,
        CancellationToken ct
    )
    {
        await _clientMessenger.SendToAll(
            "RefreshPermissions",
            "dashboardHub",
            new { userId = @event.UserId, changedBy = @event.ChangedBy }
        );

        _logger.LogInformation(
            "User permissions changed: UserId={UserId}, ChangedBy={ChangedBy}",
            [@event.UserId, @event.ChangedBy]
        );
    }
}
