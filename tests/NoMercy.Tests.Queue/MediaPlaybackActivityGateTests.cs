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
using NoMercy.NmSystem.Monitoring;
using NoMercy.Queue.MediaServer;
using NoMercyQueue;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// Pins <see cref="MediaPlaybackActivityGate"/>: "library", "file" and
/// "encoder" defer while
/// <see cref="MediaActivityMonitor.IsActive"/> is true. Scans get a capped
/// trickle window; encoder jobs wait for playback to go idle. "import"
/// and "extras" (API/DB-only work), plus the music queues, must
/// never defer, active or not.
/// </summary>
public class MediaPlaybackActivityGateTests
{
    [Theory]
    [InlineData(data: "library")]
    [InlineData(data: "file")]
    public void ShouldDefer_NasHeavyQueue_WhenActive_ReturnsTrue(string queueName)
    {
        MediaActivityMonitor monitor = new();
        monitor.Touch();
        IWorkerActivityGate gate = new MediaPlaybackActivityGate(monitor: monitor);

        gate.ShouldDefer(queueName: queueName).Should().BeTrue();
    }

    [Fact]
    public void ShouldDefer_EncoderQueue_WhilePlaybackIsActive()
    {
        MediaActivityMonitor monitor = new();
        monitor.Touch();
        DateTime now = new(year: 2026, month: 1, day: 1);
        MediaPlaybackActivityGate gate = new(
            monitor: monitor,
            maxDeferInterval: TimeSpan.FromSeconds(30),
            utcNow: () => now
        );

        gate.ShouldDefer("encoder").Should().BeTrue();
        now = now.AddMinutes(1);
        gate.ShouldDefer("encoder").Should().BeTrue();
    }

    [Theory]
    [InlineData(data: "library")]
    [InlineData(data: "file")]
    public void ShouldDefer_NasHeavyQueue_WhenInactive_ReturnsFalse(string queueName)
    {
        MediaActivityMonitor monitor = new();
        IWorkerActivityGate gate = new MediaPlaybackActivityGate(monitor: monitor);

        gate.ShouldDefer(queueName: queueName).Should().BeFalse();
    }

    [Theory]
    [InlineData(data: "import")]
    [InlineData(data: "extras")]
    [InlineData(data: "music")]
    public void ShouldDefer_ApiDbOnlyOrNonNasQueue_WhenActive_ReturnsFalse(string queueName)
    {
        MediaActivityMonitor monitor = new();
        monitor.Touch();
        IWorkerActivityGate gate = new MediaPlaybackActivityGate(monitor: monitor);

        gate.ShouldDefer(queueName: queueName).Should().BeFalse();
    }

    [Theory]
    [InlineData(data: "import")]
    [InlineData(data: "extras")]
    [InlineData(data: "encoder")]
    [InlineData(data: "music")]
    public void ShouldDefer_ApiDbOnlyOrNonNasQueue_WhenInactive_ReturnsFalse(string queueName)
    {
        MediaActivityMonitor monitor = new();
        IWorkerActivityGate gate = new MediaPlaybackActivityGate(monitor: monitor);

        gate.ShouldDefer(queueName: queueName).Should().BeFalse();
    }

    [Fact]
    public void DeferInterval_IsTwoSeconds()
    {
        MediaActivityMonitor monitor = new();
        IWorkerActivityGate gate = new MediaPlaybackActivityGate(monitor: monitor);

        gate.DeferInterval.Should().Be(expected: TimeSpan.FromSeconds(seconds: 2));
    }

    [Fact]
    public void ShouldDefer_NasHeavyQueue_AllowsOneJobThroughPerTrickleWindow()
    {
        MediaActivityMonitor monitor = new();
        monitor.Touch();
        DateTime now = new(year: 2026, month: 1, day: 1, hour: 0, minute: 0, second: 0);
        MediaPlaybackActivityGate gate = new(
            monitor: monitor,
            maxDeferInterval: TimeSpan.FromSeconds(seconds: 30),
            utcNow: () => now
        );

        // Still within the trickle window — deferred.
        gate.ShouldDefer(queueName: "library").Should().BeTrue();

        // Trickle window elapsed — exactly one poll is let through...
        now = now.AddSeconds(value: 31);
        gate.ShouldDefer(queueName: "library").Should().BeFalse();

        // ...and immediately re-armed, so the next poll defers again.
        gate.ShouldDefer(queueName: "library").Should().BeTrue();
    }

    [Fact]
    public void ShouldDefer_NasHeavyQueue_TracksTrickleWindowPerQueueIndependently()
    {
        MediaActivityMonitor monitor = new();
        monitor.Touch();
        DateTime now = new(year: 2026, month: 1, day: 1, hour: 0, minute: 0, second: 0);
        MediaPlaybackActivityGate gate = new(
            monitor: monitor,
            maxDeferInterval: TimeSpan.FromSeconds(seconds: 30),
            utcNow: () => now
        );

        // Establish "library"'s baseline, then let its window elapse.
        gate.ShouldDefer(queueName: "library").Should().BeTrue();
        now = now.AddSeconds(value: 31);
        gate.ShouldDefer(queueName: "library").Should().BeFalse();

        // "file" has never been checked before, so its own window hasn't
        // started yet — it defers independently of "library" having just
        // trickled through.
        gate.ShouldDefer(queueName: "file").Should().BeTrue();
    }
}
