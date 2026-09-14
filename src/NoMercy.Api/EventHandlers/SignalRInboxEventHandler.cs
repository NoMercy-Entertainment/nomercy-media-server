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
using NoMercy.Events.Inbox;
using NoMercy.Networking.Messaging;

namespace NoMercy.Api.EventHandlers;

public class SignalRInboxEventHandler : EventSubscriber
{
    private readonly IClientMessenger _clientMessenger;

    private readonly ILogger<SignalRInboxEventHandler> _logger;

    public SignalRInboxEventHandler(
        ILogger<SignalRInboxEventHandler> logger,
        IEventBus eventBus,
        IClientMessenger clientMessenger
    )
    {
        _logger = logger;
        _clientMessenger = clientMessenger;
        Track(eventBus.Subscribe<InboxItemDetectedEvent>(OnItemDetected));
        Track(eventBus.Subscribe<InboxItemUpdatedEvent>(OnItemUpdated));
    }

    internal async Task OnItemDetected(InboxItemDetectedEvent @event, CancellationToken ct)
    {
        await _clientMessenger.SendToAll(
            "InboxItemAdded",
            "dashboardHub",
            new
            {
                @event.Id,
                @event.DetectedType,
                @event.Confidence,
                @event.Status,
            }
        );

        _logger.LogInformation(
            "Inbox item detected: {Id} ({DetectedType}, {Confidence})",
            [@event.Id, @event.DetectedType, @event.Confidence]
        );
    }

    internal async Task OnItemUpdated(InboxItemUpdatedEvent @event, CancellationToken ct)
    {
        await _clientMessenger.SendToAll(
            "InboxItemUpdated",
            "dashboardHub",
            new { @event.Id, @event.Status }
        );

        _logger.LogInformation("Inbox item updated: {Id} → {Status}", [@event.Id, @event.Status]);
    }
}
