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
using NoMercy.Events.Onboarding;
using NoMercy.Networking.Messaging;
using NoMercy.Networking.Messaging.EventHandlers;

namespace NoMercy.Api.EventHandlers;

/// <summary>
/// Mirrors <see cref="DriveMonitorEventHandler"/>: subscribes to
/// <see cref="DiscOnboardingStateChangedEvent"/> and rebroadcasts it on
/// <c>ripperHub</c> and <c>drivesHub</c> as <c>"DiscOnboardingState"</c>.
/// </summary>
public class DiscOnboardingEventHandler : EventSubscriber
{
    private readonly IClientMessenger _clientMessenger;

    public DiscOnboardingEventHandler(IEventBus eventBus, IClientMessenger clientMessenger)
    {
        _clientMessenger = clientMessenger;
        Track(eventBus.Subscribe<DiscOnboardingStateChangedEvent>(OnDiscOnboardingStateChanged));
    }

    internal async Task OnDiscOnboardingStateChanged(
        DiscOnboardingStateChangedEvent @event,
        CancellationToken ct
    )
    {
        await _clientMessenger.SendToAll("DiscOnboardingState", "ripperHub", @event.StateData);
        await _clientMessenger.SendToAll("DiscOnboardingState", "drivesHub", @event.StateData);
    }
}
