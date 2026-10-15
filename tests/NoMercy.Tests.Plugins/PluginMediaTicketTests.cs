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
/// A link a viewer can read is a link a viewer can share, and an upstream that
/// carries a credential would be shared with it. So the host mints every one,
/// keeps the address, and binds the ticket to one account and a deadline.
/// </summary>
public class PluginMediaTicketTests
{
    private static readonly Ulid Radio = Ulid.Parse("01J9ZK5V8Y0000000000000000");
    private static readonly Guid Listener = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Housemate = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private const string Upstream = "https://stream.example.com/radio.aac?token=secret";

    private static readonly byte[] Key = "a-server-key-that-is-long-enough-for-hmac"u8.ToArray();
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static (PluginMediaTicketMinter Minter, MovableClock Clock) Build(byte[]? key = null)
    {
        MovableClock clock = new(Now);

        return (new(clock, key ?? Key), clock);
    }

    [Fact]
    public void A_fresh_ticket_reads_back_the_same_plugin_user_and_upstream()
    {
        (PluginMediaTicketMinter minter, _) = Build();

        PluginMediaTicket? read = minter.Read(
            minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5))
        );

        read!.PluginId.Should().Be(Radio);
        read.UserId.Should().Be(Listener);
        read.Upstream.Should().Be(Upstream);
        read.ExpiresAt.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public void The_ticket_never_carries_the_upstream_in_the_clear()
    {
        (PluginMediaTicketMinter minter, _) = Build();

        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5));

        ticket.Should().NotContain("stream.example.com");
        ticket.Should().NotContain("secret");
    }

    [Fact]
    public void An_edited_ticket_does_not_read()
    {
        (PluginMediaTicketMinter minter, _) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5));

        minter.Read(ticket[..^2] + "AA").Should().BeNull();
    }

    [Fact]
    public void A_ticket_whose_body_was_rewritten_does_not_read()
    {
        (PluginMediaTicketMinter minter, _) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5));
        string[] parts = ticket.Split('.');

        minter
            .Read($"{parts[0][..^2]}AA.{parts[1]}")
            .Should()
            .BeNull("changing who a ticket names is the whole reason it is signed");
    }

    [Fact]
    public void A_ticket_signed_with_another_servers_key_does_not_read()
    {
        (PluginMediaTicketMinter theirs, _) = Build(
            "a-different-server-key-entirely-here"u8.ToArray()
        );
        (PluginMediaTicketMinter ours, _) = Build();

        ours.Read(theirs.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5)))
            .Should()
            .BeNull();
    }

    [Fact]
    public void Something_that_is_not_a_ticket_does_not_read()
    {
        (PluginMediaTicketMinter minter, _) = Build();

        minter.Read("not-a-ticket").Should().BeNull();
        minter.Read("still.not.a.ticket").Should().BeNull();
        minter.Read("!!!.???").Should().BeNull();
    }

    [Fact]
    public void A_fresh_ticket_for_the_right_person_is_not_refused()
    {
        (PluginMediaTicketMinter minter, _) = Build();

        minter
            .Refuse(minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5)), Listener)
            .Should()
            .BeNull();
    }

    [Fact]
    public void An_expired_ticket_is_refused_and_says_to_open_it_again()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5));

        clock.Now = Now.AddMinutes(6);

        PluginRefusal? refusal = minter.Refuse(ticket);

        refusal!.Code.Should().Be(PluginRefusalCodes.MediaTicketExpired);
        refusal.Fix.Should().Contain("Open the item again");
    }

    [Fact]
    public void A_ticket_on_its_last_second_still_plays()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5));

        clock.Now = Now.AddMinutes(5).AddSeconds(-1);

        minter.Refuse(ticket, Listener).Should().BeNull();
    }

    [Fact]
    public void A_ticket_at_the_moment_it_expires_has_expired()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5));

        clock.Now = Now.AddMinutes(5);

        minter
            .Refuse(ticket, Listener)!
            .Code.Should()
            .Be(
                PluginRefusalCodes.MediaTicketExpired,
                "the deadline is the moment it stops working, not the last moment it works"
            );
    }

    [Fact]
    public void A_ticket_minted_for_somebody_else_is_refused_as_a_mismatch()
    {
        (PluginMediaTicketMinter minter, _) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5));

        minter
            .Refuse(ticket, Housemate)!
            .Code.Should()
            .Be(PluginRefusalCodes.MediaTicketUserMismatch);
    }

    [Fact]
    public void A_ticket_this_server_never_minted_reads_as_run_out()
    {
        (PluginMediaTicketMinter minter, _) = Build();

        minter
            .Refuse("not-a-ticket")!
            .Code.Should()
            .Be(
                PluginRefusalCodes.MediaTicketExpired,
                "telling a forgery apart from a stale link tells somebody guessing which half they got wrong"
            );
    }

    [Fact]
    public void The_address_behind_an_expired_ticket_is_not_kept()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(5));

        clock.Now = Now.AddMinutes(6);
        // Minting anything is what sweeps; a server that plays nothing for an
        // hour has nothing to sweep.
        minter.Mint(Radio, Housemate, "https://other.example.com/x.aac", TimeSpan.FromMinutes(5));

        minter
            .Read(ticket)
            .Should()
            .BeNull("a month of playbacks would otherwise hold every address a plugin handed over");
    }

    /// <summary>
    /// A live HLS player asks for the same playlist every few seconds for as
    /// long as the viewer watches. While it keeps asking, the ticket keeps
    /// playing, well past the deadline it was minted with.
    /// </summary>
    [Fact]
    public void A_ticket_that_keeps_being_played_plays_past_its_deadline()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(15));

        for (
            TimeSpan at = TimeSpan.Zero;
            at <= TimeSpan.FromHours(2);
            at += TimeSpan.FromSeconds(6)
        )
        {
            clock.Now = Now + at;

            minter.Refuse(ticket, Listener).Should().BeNull($"the player is still asking at {at}");
            minter.Played(ticket);
        }
    }

    [Fact]
    public void A_played_ticket_that_falls_silent_after_its_deadline_runs_out()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(15));

        clock.Now = Now.AddMinutes(14);
        minter.Played(ticket);

        clock.Now =
            Now.AddMinutes(14) + PluginMediaTicketMinter.IdleTimeout - TimeSpan.FromSeconds(1);
        minter.Refuse(ticket, Listener).Should().BeNull();

        clock.Now = Now.AddMinutes(14) + PluginMediaTicketMinter.IdleTimeout;
        minter
            .Refuse(ticket, Listener)!
            .Code.Should()
            .Be(PluginRefusalCodes.MediaTicketExpired, "the viewer stopped watching");
    }

    /// <summary>
    /// Being played earns more time only after the deadline. Before it, a
    /// pause of any length inside the window plays as it always did.
    /// </summary>
    [Fact]
    public void A_ticket_played_early_and_paused_still_plays_until_its_deadline()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(15));

        clock.Now = Now.AddMinutes(1);
        minter.Played(ticket);

        clock.Now = Now.AddMinutes(14);
        minter.Refuse(ticket, Listener).Should().BeNull();
    }

    [Fact]
    public void A_ticket_that_is_never_played_still_runs_out_at_its_deadline()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(15));

        clock.Now = Now.AddMinutes(15);

        minter.Refuse(ticket, Listener)!.Code.Should().Be(PluginRefusalCodes.MediaTicketExpired);
    }

    /// <summary>A link that leaked stops working within a day, even while it is used.</summary>
    [Fact]
    public void No_ticket_outlives_the_hard_limit_however_much_it_is_played()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(15));

        for (
            TimeSpan at = TimeSpan.Zero;
            at < PluginMediaTicketMinter.MaxLifetime;
            at += TimeSpan.FromMinutes(1)
        )
        {
            clock.Now = Now + at;
            minter.Played(ticket);
        }

        clock.Now = Now + PluginMediaTicketMinter.MaxLifetime;

        minter.Refuse(ticket, Listener)!.Code.Should().Be(PluginRefusalCodes.MediaTicketExpired);
    }

    /// <summary>Playing it does not change who it is for.</summary>
    [Fact]
    public void A_played_ticket_is_still_refused_to_somebody_else()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(15));

        clock.Now = Now.AddMinutes(14);
        minter.Played(ticket);
        clock.Now = Now.AddMinutes(15).AddSeconds(30);

        minter
            .Refuse(ticket, Housemate)!
            .Code.Should()
            .Be(PluginRefusalCodes.MediaTicketUserMismatch);
    }

    [Fact]
    public void Playing_something_that_is_not_a_ticket_does_nothing()
    {
        (PluginMediaTicketMinter minter, _) = Build();

        Action play = () =>
        {
            minter.Played("not-a-ticket");
            minter.Played("still.not.a.ticket");
        };

        play.Should().NotThrow();
    }

    /// <summary>The sweep keeps the address of a ticket that is still being watched.</summary>
    [Fact]
    public void The_address_behind_a_ticket_still_being_played_is_kept()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(15));

        clock.Now = Now.AddMinutes(14);
        minter.Played(ticket);
        clock.Now = Now.AddMinutes(15).AddSeconds(30);
        minter.Mint(Radio, Housemate, "https://other.example.com/x.aac", TimeSpan.FromMinutes(5));

        minter.Read(ticket)!.Upstream.Should().Be(Upstream);
    }

    /// <summary>
    /// A ticket that ran out stays out. Playing it is not a way back, or a
    /// link found in a log would be one request away from working again.
    /// </summary>
    [Fact]
    public void Playing_a_ticket_that_already_ran_out_does_not_bring_it_back()
    {
        (PluginMediaTicketMinter minter, MovableClock clock) = Build();
        string ticket = minter.Mint(Radio, Listener, Upstream, TimeSpan.FromMinutes(15));

        clock.Now = Now.AddMinutes(16);
        minter.Played(ticket);

        minter.Refuse(ticket, Listener)!.Code.Should().Be(PluginRefusalCodes.MediaTicketExpired);
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
