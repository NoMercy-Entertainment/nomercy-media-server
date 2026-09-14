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
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.Networking.Messaging;

namespace NoMercy.Api.EventHandlers;

public class SignalRLibraryScanEventHandler : EventSubscriber
{
    private readonly IClientMessenger _clientMessenger;

    private readonly ILogger<SignalRLibraryScanEventHandler> _logger;

    public SignalRLibraryScanEventHandler(
        ILogger<SignalRLibraryScanEventHandler> logger,
        IEventBus eventBus,
        IClientMessenger clientMessenger
    )
    {
        _logger = logger;
        _clientMessenger = clientMessenger;
        Track(eventBus.Subscribe<LibraryScanStartedEvent>(OnScanStarted));
        Track(eventBus.Subscribe<LibraryScanCompletedEvent>(OnScanCompleted));
        Track(eventBus.Subscribe<MediaAddedEvent>(OnMediaAdded));
        Track(eventBus.Subscribe<MediaRemovedEvent>(OnMediaRemoved));
    }

    internal async Task OnScanStarted(LibraryScanStartedEvent @event, CancellationToken ct)
    {
        await _clientMessenger.SendToAll(
            "LibraryScanStarted",
            "dashboardHub",
            new
            {
                LibraryId = @event.LibraryId.ToString(),
                @event.LibraryName,
                @event.Timestamp,
            }
        );

        _logger.LogInformation("Library scan started: {LibraryName}", @event.LibraryName);
    }

    internal async Task OnScanCompleted(LibraryScanCompletedEvent @event, CancellationToken ct)
    {
        await _clientMessenger.SendToAll(
            "LibraryScanCompleted",
            "dashboardHub",
            new
            {
                LibraryId = @event.LibraryId.ToString(),
                @event.LibraryName,
                @event.ItemsFound,
                Duration = @event.Duration.TotalSeconds,
                @event.Timestamp,
            }
        );

        _logger.LogInformation(
            "Library scan completed: {LibraryName}, {ItemsFound} items found",
            [@event.LibraryName, @event.ItemsFound]
        );
    }

    internal async Task OnMediaAdded(MediaAddedEvent @event, CancellationToken ct)
    {
        await _clientMessenger.SendToAll(
            "MediaAdded",
            "dashboardHub",
            new
            {
                @event.MediaId,
                @event.MediaType,
                @event.Title,
                LibraryId = @event.LibraryId.ToString(),
                @event.Timestamp,
            }
        );
    }

    internal async Task OnMediaRemoved(MediaRemovedEvent @event, CancellationToken ct)
    {
        await _clientMessenger.SendToAll(
            "MediaRemoved",
            "dashboardHub",
            new
            {
                @event.MediaId,
                @event.MediaType,
                @event.Title,
                LibraryId = @event.LibraryId.ToString(),
                @event.Timestamp,
            }
        );
    }
}
