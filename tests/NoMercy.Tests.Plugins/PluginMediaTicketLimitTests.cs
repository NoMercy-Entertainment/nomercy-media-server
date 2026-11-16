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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Media;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// What keeps the tickets a server holds bounded: a cap per holder (one
/// account or one LAN device, on one plugin) that drops old, never-played
/// tickets first, and a sweep that runs now and then rather than on every
/// mint.
/// </summary>
public class PluginMediaTicketLimitTests
{
    private const int Cap = PluginMediaTicketMinter.MaxLiveTicketsPerHolder;

    private static readonly Ulid LiveTv = Ulid.Parse("01J9ZK5V8Y00000000000000D1");
    private static readonly Guid Viewer = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = PluginMediaProxy.TicketLifetime;

    [Fact]
    public void A_holder_past_the_cap_loses_its_oldest_never_played_tickets_first()
    {
        (PluginMediaTicketMinter minter, _) = Build();
        List<string> tickets = MintForDevice(minter, "tuner", Cap);

        string newest = MintOneForDevice(minter, "tuner");

        minter.ReadForDevice(tickets[0], LiveTv).Should().BeNull("it was the oldest, never played");
        minter
            .ReadForDevice(tickets[PluginMediaTicketMinter.EvictionBatch - 1], LiveTv)
            .Should()
            .BeNull();
        minter
            .ReadForDevice(tickets[PluginMediaTicketMinter.EvictionBatch], LiveTv)
            .Should()
            .NotBeNull();
        minter.ReadForDevice(tickets[^1], LiveTv).Should().NotBeNull();
        minter.ReadForDevice(newest, LiveTv).Should().NotBeNull();
        minter.HeldCount.Should().Be(Cap - PluginMediaTicketMinter.EvictionBatch + 1);
    }

    [Fact]
    public void A_ticket_being_played_is_never_dropped_for_room()
    {
        (PluginMediaTicketMinter minter, _) = Build();
        List<string> tickets = MintForDevice(minter, "tuner", Cap);
        minter.Played(tickets[0]);

        MintOneForDevice(minter, "tuner");

        minter.ReadForDevice(tickets[0], LiveTv).Should().NotBeNull("it is being played");
        minter.ReadForDevice(tickets[1], LiveTv).Should().BeNull();
    }

    [Fact]
    public void A_holder_whose_every_ticket_is_being_played_is_refused_more()
    {
        (PluginMediaTicketMinter minter, _) = Build();

        foreach (string ticket in MintForDevice(minter, "tuner", Cap))
            minter.Played(ticket);

        Action mint = () => MintOneForDevice(minter, "tuner");

        mint.Should()
            .Throw<PluginRefusedException>()
            .Which.Refusal.Code.Should()
            .Be(PluginRefusalCodes.ResourceCeiling);
    }

    [Fact]
    public void One_holder_at_the_cap_leaves_every_other_holder_alone()
    {
        (PluginMediaTicketMinter minter, _) = Build();
        string bedroom = MintOneForDevice(minter, "bedroom");
        string viewer = minter.Mint(LiveTv, Viewer, "https://iptv.example.com/1.ts", Lifetime);

        MintForDevice(minter, "tuner", Cap + 1);

        minter.ReadForDevice(bedroom, LiveTv).Should().NotBeNull();
        minter.Refuse(viewer, Viewer).Should().BeNull();
    }

    [Fact]
    public void A_users_tickets_are_capped_the_same_way()
    {
        (PluginMediaTicketMinter minter, _) = Build();
        string first = minter.Mint(LiveTv, Viewer, "https://iptv.example.com/0.ts", Lifetime);

        for (int index = 1; index <= Cap; index++)
            minter.Mint(LiveTv, Viewer, $"https://iptv.example.com/{index}.ts", Lifetime);

        minter.Read(first).Should().BeNull();
        minter.HeldCount.Should().BeLessThanOrEqualTo(Cap);
    }

    [Fact]
    public void The_sweep_waits_for_its_interval_rather_than_running_on_every_mint()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string expired = minter.Mint(LiveTv, Viewer, "https://a/1.ts", TimeSpan.FromSeconds(10));

        clock.Now = Now.AddSeconds(30);
        minter.Mint(LiveTv, Viewer, "https://a/2.ts", Lifetime);

        minter.HeldCount.Should().Be(2, "the sweep is not due yet");
        minter.Refuse(expired, Viewer).Should().NotBeNull("an unswept ticket still does not play");

        clock.Now = Now.AddSeconds(30) + PluginMediaTicketMinter.SweepInterval;
        minter.Mint(LiveTv, Viewer, "https://a/3.ts", Lifetime);

        minter.Read(expired).Should().BeNull("the interval passed, so this mint swept");
        minter.HeldCount.Should().Be(2);
    }

    [Fact]
    public void The_sweep_also_runs_after_enough_mints()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string expired = minter.Mint(LiveTv, Viewer, "https://a/0.ts", TimeSpan.FromSeconds(10));
        clock.Now = Now.AddSeconds(30);

        for (int index = 1; index < PluginMediaTicketMinter.SweepEvery; index++)
            MintOneForDevice(minter, "tuner");

        minter.Read(expired).Should().NotBeNull("not swept yet");

        MintOneForDevice(minter, "tuner");

        minter.Read(expired).Should().BeNull();
    }

    private static (PluginMediaTicketMinter Minter, MovableClock Clock) Build()
    {
        MovableClock clock = new(Now);

        return (new(clock, "a-server-key-that-is-long-enough-for-hmac"u8.ToArray()), clock);
    }

    private static List<string> MintForDevice(
        PluginMediaTicketMinter minter,
        string device,
        int count
    )
    {
        List<string> tickets = [];

        for (int index = 0; index < count; index++)
            tickets.Add(MintOneForDevice(minter, device));

        return tickets;
    }

    private static string MintOneForDevice(PluginMediaTicketMinter minter, string device) =>
        minter.MintForDevice(
            LiveTv,
            device,
            new() { Links = [new() { Url = new("https://iptv.example.com/segment.ts") }] },
            Lifetime
        );

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
