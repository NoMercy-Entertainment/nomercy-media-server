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
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Quotas;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Counted where the server hands the bytes over, which is the only place a
/// number is a fact rather than an estimate. Download is not metered: a
/// plugin fetching spends the owner's own bandwidth on something they
/// installed, and a plugin sending spends their uplink on somebody else.
/// </summary>
public class PluginQuotaMeterTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000007");
    private static readonly Ulid Other = Ulid.Parse("01J9ZK5V8Y0000000000000008");
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private const long Megabyte = 1024 * 1024;

    private static (PluginQuotaMeter Meter, MovableClock Clock) Build(
        long diskMb = 10,
        long uploadKb = 100
    )
    {
        (PluginQuotaMeter meter, MovableClock clock, _) = BuildCounted(diskMb, uploadKb);

        return (meter, clock);
    }

    private static (
        PluginQuotaMeter Meter,
        MovableClock Clock,
        RecordingCounter Counter
    ) BuildCounted(long diskMb = 10, long uploadKb = 100)
    {
        MovableClock clock = new(Noon);
        RecordingCounter counter = new();

        return (
            new(
                new StubQuotas(new(50, 512 * Megabyte, diskMb * Megabyte, uploadKb * 1024)),
                clock,
                counter
            ),
            clock,
            counter
        );
    }

    [Fact]
    public void A_write_inside_the_allowance_is_allowed_and_counted()
    {
        (PluginQuotaMeter meter, _) = Build(diskMb: 10);

        meter.AccountDisk(Plugin, 4 * Megabyte).Should().BeNull();

        meter.DiskUsed(Plugin).Should().Be(4 * Megabyte);
    }

    [Fact]
    public void Writes_add_up()
    {
        (PluginQuotaMeter meter, _) = Build(diskMb: 10);

        meter.AccountDisk(Plugin, 4 * Megabyte);
        meter.AccountDisk(Plugin, 4 * Megabyte);

        meter.DiskUsed(Plugin).Should().Be(8 * Megabyte);
    }

    [Fact]
    public void A_write_that_would_go_past_the_allowance_is_refused_before_it_lands()
    {
        (PluginQuotaMeter meter, _) = Build(diskMb: 10);
        meter.AccountDisk(Plugin, 8 * Megabyte);

        PluginRefusal? refusal = meter.AccountDisk(Plugin, 4 * Megabyte);

        refusal!.Code.Should().Be(PluginRefusalCodes.QuotaDiskExceeded);
        refusal.Severity.Should().Be(PluginRefusalSeverity.Blocked);
        meter
            .DiskUsed(Plugin)
            .Should()
            .Be(8 * Megabyte, "a refused write must not be counted as if it happened");
    }

    [Fact]
    public void A_write_that_exactly_fills_the_allowance_is_allowed()
    {
        (PluginQuotaMeter meter, _) = Build(diskMb: 10);

        meter
            .AccountDisk(Plugin, 10 * Megabyte)
            .Should()
            .BeNull("an allowance is what a plugin may use, not what it may not reach");
    }

    [Fact]
    public void The_refusal_says_how_much_the_plugin_was_allowed()
    {
        (PluginQuotaMeter meter, _) = Build(diskMb: 10);

        meter.AccountDisk(Plugin, 20 * Megabyte)!.What.Should().Contain("10 MB");
    }

    [Fact]
    public void Removing_something_makes_room_again()
    {
        (PluginQuotaMeter meter, _) = Build(diskMb: 10);
        meter.AccountDisk(Plugin, 9 * Megabyte);

        meter.ReleaseDisk(Plugin, 5 * Megabyte);

        meter.DiskUsed(Plugin).Should().Be(4 * Megabyte);
        meter.AccountDisk(Plugin, 5 * Megabyte).Should().BeNull();
    }

    [Fact]
    public void Removing_more_than_was_written_does_not_go_below_nothing()
    {
        (PluginQuotaMeter meter, _) = Build(diskMb: 10);
        meter.AccountDisk(Plugin, 2 * Megabyte);

        meter.ReleaseDisk(Plugin, 9 * Megabyte);

        meter
            .DiskUsed(Plugin)
            .Should()
            .Be(0, "a negative total would hand a plugin more than its allowance");
    }

    [Fact]
    public void One_plugins_disk_is_not_anothers()
    {
        (PluginQuotaMeter meter, _) = Build(diskMb: 10);
        meter.AccountDisk(Plugin, 9 * Megabyte);

        meter.AccountDisk(Other, 9 * Megabyte).Should().BeNull();
        meter.DiskUsed(Other).Should().Be(9 * Megabyte);
    }

    [Fact]
    public void Sending_inside_the_rate_is_allowed()
    {
        (PluginQuotaMeter meter, _) = Build(uploadKb: 100);

        meter.AccountUpload(Plugin, 50 * 1024).Should().BeNull();
    }

    [Fact]
    public void Sending_past_the_rate_in_one_second_is_refused()
    {
        (PluginQuotaMeter meter, _) = Build(uploadKb: 100);
        meter.AccountUpload(Plugin, 60 * 1024);

        PluginRefusal? refusal = meter.AccountUpload(Plugin, 60 * 1024);

        refusal!.Code.Should().Be(PluginRefusalCodes.QuotaUploadExceeded);
        refusal
            .Severity.Should()
            .Be(
                PluginRefusalSeverity.Degraded,
                "a plugin sending too fast is slowed, not stopped: it is still doing what it was installed for"
            );
    }

    [Fact]
    public void Sending_exactly_the_rate_is_allowed()
    {
        (PluginQuotaMeter meter, _) = Build(uploadKb: 100);

        meter
            .AccountUpload(Plugin, 100 * 1024)
            .Should()
            .BeNull("an allowance is what a plugin may use, not what it may not reach");
    }

    [Fact]
    public void The_next_second_starts_again()
    {
        (PluginQuotaMeter meter, MovableClock clock) = Build(uploadKb: 100);
        meter.AccountUpload(Plugin, 90 * 1024);

        clock.Now = Noon.AddSeconds(1);

        meter
            .AccountUpload(Plugin, 90 * 1024)
            .Should()
            .BeNull("an uplink allowance is a rate, so yesterday's traffic used nothing up");
    }

    [Fact]
    public void Two_sends_inside_one_second_are_added_together()
    {
        (PluginQuotaMeter meter, MovableClock clock) = Build(uploadKb: 100);
        meter.AccountUpload(Plugin, 60 * 1024);

        clock.Now = Noon.AddMilliseconds(400);

        meter.AccountUpload(Plugin, 60 * 1024).Should().NotBeNull();
    }

    [Fact]
    public void One_plugins_sending_is_not_anothers()
    {
        (PluginQuotaMeter meter, _) = Build(uploadKb: 100);
        meter.AccountUpload(Plugin, 90 * 1024);

        meter.AccountUpload(Other, 90 * 1024).Should().BeNull();
    }

    [Fact]
    public void An_allowance_the_owner_turned_off_meters_nothing()
    {
        PluginQuotaMeter meter = new(new StubQuotas(PluginQuota.Unlimited), new MovableClock(Noon));

        meter.AccountDisk(Plugin, long.MaxValue / 2).Should().BeNull();
        meter.AccountUpload(Plugin, long.MaxValue / 2).Should().BeNull();
    }

    [Fact]
    public void The_default_is_a_share_of_the_machine_rather_than_a_fixed_number()
    {
        PluginQuota small = PluginQuota.DefaultFor(4, 8L * 1024 * 1024 * 1024, 1_000_000);
        PluginQuota large = PluginQuota.DefaultFor(32, 128L * 1024 * 1024 * 1024, 8_000_000);

        small.CpuPercent.Should().Be(100, "a quarter of four processors is one whole one");
        large.CpuPercent.Should().Be(800);
        large.MemoryBytes.Should().BeGreaterThan(small.MemoryBytes);
        large.UploadBytesPerSecond.Should().Be(2_000_000);
    }

    [Fact]
    public void The_memory_share_has_a_floor_so_a_small_machine_still_runs_plugins()
    {
        PluginQuota tiny = PluginQuota.DefaultFor(2, 1L * 1024 * 1024 * 1024, 1_000_000);

        tiny.MemoryBytes.Should()
            .Be(
                256 * Megabyte,
                "a tenth of a gigabyte is a plugin that cannot hold a playlist, which is a plugin that does not work"
            );
    }

    [Fact]
    public void The_disk_default_is_the_same_on_every_machine()
    {
        PluginQuota
            .DefaultFor(2, 1L * 1024 * 1024 * 1024, 1_000_000)
            .DiskBytes.Should()
            .Be(
                5L * 1024 * 1024 * 1024,
                "a share of the disk would let a plugin grow into a drive somebody bought for films"
            );
    }

    [Fact]
    public void A_refusal_is_counted_so_a_plugin_in_a_loop_can_be_named()
    {
        (PluginQuotaMeter meter, _, RecordingCounter counter) = BuildCounted(
            diskMb: 10,
            uploadKb: 100
        );

        meter.AccountDisk(Plugin, 20 * Megabyte);
        meter.AccountUpload(Plugin, 200 * 1024);

        counter
            .Codes.Should()
            .Equal(PluginRefusalCodes.QuotaDiskExceeded, PluginRefusalCodes.QuotaUploadExceeded);
    }

    [Fact]
    public void An_allowed_write_is_not_counted_as_a_refusal()
    {
        (PluginQuotaMeter meter, _, RecordingCounter counter) = BuildCounted(diskMb: 10);

        meter.AccountDisk(Plugin, 1 * Megabyte);

        counter.Codes.Should().BeEmpty();
    }

    [Fact]
    public void Fetching_is_never_metered()
    {
        (PluginQuotaMeter meter, _) = Build(uploadKb: 100);

        meter
            .AccountDownload(Plugin, long.MaxValue)
            .Should()
            .BeNull("a plugin fetching spends the owner's own line on what they installed");
    }

    [Fact]
    public void An_uplink_nobody_measured_is_not_capped()
    {
        PluginQuotaStore
            .Machine(null)
            .UploadBytesPerSecond.Should()
            .Be(
                long.MaxValue,
                "a cap worked out from a number the server made up slows a plugin for no reason"
            );
    }

    [Fact]
    public void A_measured_uplink_gives_the_plugin_a_quarter_of_it()
    {
        PluginQuotaStore.Machine(1_000_000).UploadBytesPerSecond.Should().Be(250_000);
    }

    [Fact]
    public async Task Sending_past_the_rate_is_slowed_rather_than_cut_off()
    {
        (PluginQuotaMeter meter, MovableClock clock, RecordingCounter counter) = BuildCounted(
            uploadKb: 1
        );
        await using PluginUploadMeteredStream stream = new(
            new MemoryStream(new byte[8 * 1024]),
            Plugin,
            meter,
            clock
        );

        byte[] buffer = new byte[8 * 1024];

        (await stream.ReadAsync(buffer))
            .Should()
            .Be(8 * 1024, "a viewer waiting is watching; a viewer refused has stopped");

        counter
            .Codes.Should()
            .ContainSingle(
                "bytes that reach a client without being counted are an allowance nothing enforces"
            )
            .Which.Should()
            .Be(PluginRefusalCodes.QuotaUploadExceeded);
    }

    private sealed class RecordingCounter : IPluginRefusalCounter
    {
        public List<string> Codes { get; } = [];

        public void Count(Ulid pluginId, PluginRefusal refusal) => Codes.Add(refusal.Code);

        public IReadOnlyList<KeyValuePair<string, int>> Counts(Ulid pluginId) => [];

        public void Clear(Ulid pluginId) { }
    }

    private sealed class StubQuotas(PluginQuota quota) : IPluginQuotaSource
    {
        public PluginQuota For(Ulid pluginId) => quota;
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
