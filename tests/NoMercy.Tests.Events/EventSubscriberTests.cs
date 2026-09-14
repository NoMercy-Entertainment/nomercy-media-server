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

using FluentAssertions;
using NoMercy.Events;
using Xunit;

namespace NoMercy.Tests.Events;

[Trait("Category", "Unit")]
public class EventSubscriberTests
{
    private sealed class PingEvent : EventBase
    {
        public override string Source => "test";
    }

    private sealed class PingCounter : EventSubscriber
    {
        public int Received { get; private set; }

        public PingCounter(IEventBus eventBus)
        {
            Track(
                eventBus.Subscribe<PingEvent>(
                    (_, _) =>
                    {
                        Received++;
                        return Task.CompletedTask;
                    }
                )
            );
        }
    }

    [Fact]
    public async Task Dispose_EndsEveryTrackedSubscription()
    {
        InMemoryEventBus bus = new();
        PingCounter counter = new(bus);

        await bus.PublishAsync(new PingEvent());
        counter.Dispose();
        await bus.PublishAsync(new PingEvent());

        counter.Received.Should().Be(1);
    }
}
