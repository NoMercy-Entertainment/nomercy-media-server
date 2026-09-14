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
using NoMercy.Events.Library;
using NoMercy.Networking.Dto;
using NoMercy.Networking.Messaging;

namespace NoMercy.Api.EventHandlers;

public class SignalRLibraryRefreshEventHandler : EventSubscriber
{
    private readonly IClientMessenger _clientMessenger;

    public SignalRLibraryRefreshEventHandler(IEventBus eventBus, IClientMessenger clientMessenger)
    {
        _clientMessenger = clientMessenger;
        Track(eventBus.Subscribe<LibraryRefreshedEvent>(OnLibraryRefresh));
    }

    internal async Task OnLibraryRefresh(LibraryRefreshedEvent @event, CancellationToken ct)
    {
        await _clientMessenger.SendToAll(
            "RefreshLibrary",
            "videoHub",
            new RefreshLibraryDto { QueryKey = @event.QueryKey }
        );
    }
}
