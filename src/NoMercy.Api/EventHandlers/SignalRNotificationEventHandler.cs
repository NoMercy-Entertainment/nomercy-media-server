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

using NoMercy.Events;
using NoMercy.Events.Media;
using NoMercy.Networking.Dto;
using NoMercy.Networking.Messaging;

namespace NoMercy.Api.EventHandlers;

public class SignalRNotificationEventHandler : EventSubscriber
{
    private readonly IClientMessenger _clientMessenger;

    public SignalRNotificationEventHandler(IEventBus eventBus, IClientMessenger clientMessenger)
    {
        _clientMessenger = clientMessenger;
        Track(eventBus.Subscribe<UserNotifiedEvent>(OnUserNotification));
    }

    // Broadcast only. A notification aimed at one user goes through
    // NotificationDispatcher, which picks SignalR or push so the user is told
    // exactly once; delivering it here as well would be the second copy.
    internal async Task OnUserNotification(UserNotifiedEvent @event, CancellationToken ct)
    {
        if (@event.UserId is not null)
            return;

        NotifyDto payload = new()
        {
            Title = @event.Title,
            Message = @event.Message,
            Type = @event.Type,
            Route = @event.Route,
        };

        await _clientMessenger.SendToAll("Notify", @event.Hub, payload);
    }
}
