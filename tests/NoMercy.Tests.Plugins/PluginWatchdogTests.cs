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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Watchdog;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// CPU is throttled and memory restarts, because they fail differently. A
/// plugin burning CPU is still answering, so slowing it keeps it working. A
/// plugin holding memory is not going to give it back, and waiting for it to
/// is waiting for the whole server to run out.
/// </summary>
public class PluginWatchdogTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000005");
    private static readonly Ulid Other = Ulid.Parse("01J9ZK5V8Y0000000000000006");
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private const long Megabyte = 1024 * 1024;

    private static (
        PluginWatchdog Watchdog,
        RecordingLifecycle Lifecycle,
        MovableClock Clock
    ) Build(double cpu = 50, long memoryMb = 512)
    {
        RecordingLifecycle lifecycle = new();
        MovableClock clock = new(Noon);

        return (
            new(new StubCeilings(new(cpu, memoryMb * Megabyte)), lifecycle, clock),
            lifecycle,
            clock
        );
    }

    private static PluginResourceSample Sample(double cpu = 10, long memoryMb = 100) =>
        new(cpu, memoryMb * Megabyte);

    [Fact]
    public void Under_both_ceilings_nothing_happens()
    {
        (PluginWatchdog watchdog, RecordingLifecycle lifecycle, _) = Build();

        watchdog.Observe(Plugin, Sample()).Should().Be(PluginWatchdogAction.None);

        lifecycle.Throttled.Should().BeEmpty();
        lifecycle.Restarts.Should().BeEmpty();
        lifecycle.Disabled.Should().BeEmpty();
        watchdog.LastRefusal(Plugin).Should().BeNull();
    }

    [Fact]
    public void Exactly_on_a_ceiling_is_not_over_it()
    {
        (PluginWatchdog watchdog, _, _) = Build(cpu: 50, memoryMb: 512);

        watchdog
            .Observe(Plugin, new(50, 512 * Megabyte))
            .Should()
            .Be(
                PluginWatchdogAction.None,
                "an allowance is what a plugin may use, not what it may not reach"
            );
    }

    [Fact]
    public void Over_the_processor_ceiling_the_plugin_is_slowed_rather_than_stopped()
    {
        (PluginWatchdog watchdog, RecordingLifecycle lifecycle, _) = Build(cpu: 50);

        watchdog.Observe(Plugin, Sample(cpu: 90)).Should().Be(PluginWatchdogAction.ThrottleCpu);

        lifecycle.Throttled.Should().ContainSingle().Which.Should().Be(Plugin);
        lifecycle.Restarts.Should().BeEmpty("a plugin burning processor is still answering");
    }

    [Fact]
    public void A_processor_breach_says_what_it_used_and_what_it_was_allowed()
    {
        (PluginWatchdog watchdog, _, _) = Build(cpu: 50);

        watchdog.Observe(Plugin, Sample(cpu: 90));

        PluginRefusal refusal = watchdog.LastRefusal(Plugin)!;
        refusal.Code.Should().Be(PluginRefusalCodes.ResourceCeiling);
        refusal.Severity.Should().Be(PluginRefusalSeverity.Degraded);
        refusal.What.Should().Contain("90%").And.Contain("50%");
    }

    [Fact]
    public void Over_the_memory_ceiling_the_plugin_is_restarted()
    {
        (PluginWatchdog watchdog, RecordingLifecycle lifecycle, _) = Build(memoryMb: 512);

        watchdog.Observe(Plugin, Sample(memoryMb: 900)).Should().Be(PluginWatchdogAction.Restart);

        lifecycle.Restarts.Should().ContainSingle().Which.Should().Be(Plugin);
        lifecycle.Throttled.Should().BeEmpty("memory is not given back by asking");
    }

    [Fact]
    public void A_memory_breach_says_what_it_held_in_megabytes()
    {
        (PluginWatchdog watchdog, _, _) = Build(memoryMb: 512);

        watchdog.Observe(Plugin, Sample(memoryMb: 900));

        watchdog.LastRefusal(Plugin)!.What.Should().Contain("900 MB").And.Contain("512 MB");
    }

    [Fact]
    public void Memory_is_answered_before_processor_when_both_are_over()
    {
        (PluginWatchdog watchdog, RecordingLifecycle lifecycle, _) = Build(cpu: 50, memoryMb: 512);

        watchdog
            .Observe(Plugin, new(90, 900 * Megabyte))
            .Should()
            .Be(
                PluginWatchdogAction.Restart,
                "slowing down a plugin that is about to exhaust the server's memory changes nothing"
            );

        lifecycle.Throttled.Should().BeEmpty();
    }

    [Fact]
    public void Three_restarts_in_an_hour_turn_the_plugin_off()
    {
        (PluginWatchdog watchdog, RecordingLifecycle lifecycle, _) = Build(memoryMb: 512);
        PluginResourceSample tooBig = Sample(memoryMb: 900);

        watchdog.Observe(Plugin, tooBig).Should().Be(PluginWatchdogAction.Restart);
        watchdog.Observe(Plugin, tooBig).Should().Be(PluginWatchdogAction.Restart);
        watchdog.Observe(Plugin, tooBig).Should().Be(PluginWatchdogAction.Restart);
        watchdog.Observe(Plugin, tooBig).Should().Be(PluginWatchdogAction.Disable);

        lifecycle.Restarts.Should().HaveCount(3);
        lifecycle.Disabled.Should().ContainSingle().Which.Should().Be(Plugin);
    }

    [Fact]
    public void Turning_it_off_says_how_many_restarts_it_took()
    {
        (PluginWatchdog watchdog, _, _) = Build(memoryMb: 512);
        PluginResourceSample tooBig = Sample(memoryMb: 900);

        for (int attempt = 0; attempt < 4; attempt++)
            watchdog.Observe(Plugin, tooBig);

        PluginRefusal refusal = watchdog.LastRefusal(Plugin)!;
        refusal.Code.Should().Be(PluginRefusalCodes.DisabledAfterRestarts);
        refusal.Severity.Should().Be(PluginRefusalSeverity.Blocked);
        refusal.What.Should().Contain("3 times");
        refusal.Fix.Should().Contain("Turn it on again");
    }

    [Fact]
    public void A_restart_an_hour_ago_does_not_count_toward_the_three()
    {
        (PluginWatchdog watchdog, RecordingLifecycle lifecycle, MovableClock clock) = Build(
            memoryMb: 512
        );
        PluginResourceSample tooBig = Sample(memoryMb: 900);

        watchdog.Observe(Plugin, tooBig);
        watchdog.Observe(Plugin, tooBig);
        watchdog.Observe(Plugin, tooBig);

        clock.Now = Noon.AddHours(1);

        watchdog
            .Observe(Plugin, tooBig)
            .Should()
            .Be(PluginWatchdogAction.Restart, "a plugin that behaved for an hour is not in a loop");

        lifecycle.Disabled.Should().BeEmpty();
    }

    [Fact]
    public void One_plugins_restarts_are_not_anothers()
    {
        (PluginWatchdog watchdog, RecordingLifecycle lifecycle, _) = Build(memoryMb: 512);
        PluginResourceSample tooBig = Sample(memoryMb: 900);

        watchdog.Observe(Plugin, tooBig);
        watchdog.Observe(Plugin, tooBig);
        watchdog.Observe(Plugin, tooBig);

        watchdog.Observe(Other, tooBig).Should().Be(PluginWatchdogAction.Restart);

        lifecycle.Disabled.Should().BeEmpty();
    }

    [Fact]
    public void A_plugin_with_no_allowance_set_gets_the_servers_default()
    {
        PluginResourceCeilings.Default.CpuPercent.Should().Be(50);
        PluginResourceCeilings.Default.MemoryBytes.Should().Be(512 * Megabyte);
    }

    private sealed class StubCeilings(PluginResourceCeilings ceilings)
        : IPluginResourceCeilingSource
    {
        public PluginResourceCeilings For(Ulid pluginId) => ceilings;
    }

    private sealed class RecordingLifecycle : IPluginWatchdogLifecycle
    {
        public List<Ulid> Throttled { get; } = [];
        public List<Ulid> Restarts { get; } = [];
        public List<Ulid> Disabled { get; } = [];

        public void Throttle(Ulid pluginId) => Throttled.Add(pluginId);

        public void Restart(Ulid pluginId) => Restarts.Add(pluginId);

        public void Disable(Ulid pluginId) => Disabled.Add(pluginId);
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
