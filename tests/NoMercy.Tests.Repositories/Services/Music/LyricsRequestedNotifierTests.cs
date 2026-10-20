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
using Moq;
using NoMercy.Data.Services.Music;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Tests.Repositories.Services.Music;

/// <summary>
/// The lyrics-requested topic is one cue per track per day, raised by the
/// host, and a broken bus never fails the request that raised it (#620).
/// </summary>
[Trait("Category", "Unit")]
public class LyricsRequestedNotifierTests
{
    [Fact]
    public async Task Publishes_the_topic_from_the_host_once_per_track_inside_the_window()
    {
        Mock<IEventBus> bus = new();
        LyricsRequestedNotifier notifier = new(bus.Object, TimeSpan.FromHours(24));
        Guid track = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        (await notifier.NotifyAsync(track)).Should().BeTrue();
        (await notifier.NotifyAsync(track))
            .Should()
            .BeFalse("the second play is inside the window");
        (await notifier.NotifyAsync(other)).Should().BeTrue("the window is per track");

        bus.Verify(
            b =>
                b.PublishAsync(
                    It.Is<PluginMessageEvent>(e =>
                        e.Name == PluginTopics.LyricsRequested
                        && e.PluginId == Ulid.Empty
                        && e.Payload!.ToJsonString().Contains(track.ToString())
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        bus.Verify(
            b => b.PublishAsync(It.IsAny<PluginMessageEvent>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task Publishes_again_once_the_window_has_passed()
    {
        Mock<IEventBus> bus = new();
        LyricsRequestedNotifier notifier = new(bus.Object, TimeSpan.FromMilliseconds(20));
        Guid track = Guid.NewGuid();

        await notifier.NotifyAsync(track);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        (await notifier.NotifyAsync(track)).Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_bus_is_logged_and_does_not_throw()
    {
        Mock<IEventBus> bus = new();
        bus.Setup(b =>
                b.PublishAsync(It.IsAny<PluginMessageEvent>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new InvalidOperationException("bus down"));
        LyricsRequestedNotifier notifier = new(bus.Object, TimeSpan.FromHours(24));

        Func<Task<bool>> notify = () => notifier.NotifyAsync(Guid.NewGuid());

        (await notify.Should().NotThrowAsync()).Which.Should().BeFalse();
    }
}
