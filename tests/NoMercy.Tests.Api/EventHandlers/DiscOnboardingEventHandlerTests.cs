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
using NoMercy.Events;
using NoMercy.Events.Onboarding;
using NoMercy.Networking.Messaging;
using NoMercy.OpticalMedia.Onboarding;
using Xunit;

namespace NoMercy.Tests.Api.EventHandlers;

[Trait("Category", "Unit")]
public class DiscOnboardingEventHandlerTests
{
    [Fact]
    public async Task OnDiscOnboardingStateChanged_SendsDiscOnboardingStateToRipperHubAndDrivesHub()
    {
        Mock<IEventBus> eventBus = new();
        eventBus
            .Setup(b =>
                b.Subscribe<DiscOnboardingStateChangedEvent>(
                    It.IsAny<Func<DiscOnboardingStateChangedEvent, CancellationToken, Task>>()
                )
            )
            .Returns(Mock.Of<IDisposable>());
        Mock<IClientMessenger> clientMessenger = new();

        using DiscOnboardingEventHandler handler = new(eventBus.Object, clientMessenger.Object);

        DiscOnboardingSession session = DiscOnboardingSession.Create("D:\\");
        DiscOnboardingStatePayload payload = DiscOnboardingStatePayload.From(session);
        DiscOnboardingStateChangedEvent @event = new() { StateData = payload };

        await handler.OnDiscOnboardingStateChanged(@event, CancellationToken.None);

        clientMessenger.Verify(
            m => m.SendToAll("DiscOnboardingState", "ripperHub", payload),
            Times.Once
        );
        clientMessenger.Verify(
            m => m.SendToAll("DiscOnboardingState", "drivesHub", payload),
            Times.Once
        );
    }
}
