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
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginUiContractTests
{
    [Fact]
    public void Every_kind_declares_its_slots()
    {
        PluginSlots
            .All.Where(slot => slot.Kind == PluginKind.Music)
            .Select(slot => slot.Slot)
            .Should()
            .BeEquivalentTo(["nav", "home-row", "artist-tab", "album-tab", "player-panel"]);

        PluginSlots
            .All.Where(slot => slot.Kind == PluginKind.Video)
            .Select(slot => slot.Slot)
            .Should()
            .BeEquivalentTo([
                "nav",
                "home-row",
                "detail-tab",
                "player-panel",
                "live",
                "guide",
                "channel-strip",
            ]);

        PluginSlots
            .All.Where(slot => slot.Kind == PluginKind.Settings)
            .Select(slot => slot.Slot)
            .Should()
            .BeEquivalentTo(["section"]);
    }

    [Fact]
    public void The_slot_list_is_twenty_entries_and_every_kind_is_covered()
    {
        PluginSlots.All.Should().HaveCount(20);
        PluginSlots
            .All.Select(slot => slot.Kind)
            .Distinct()
            .Should()
            .BeEquivalentTo(
                PluginKind.All,
                "a kind with no slot is a plugin that declares itself and is drawn nowhere"
            );
    }

    [Fact]
    public void An_unknown_slot_for_a_known_kind_is_not_known()
    {
        PluginSlots.IsKnown(PluginKind.Music, "home-row").Should().BeTrue();
        PluginSlots
            .IsKnown(PluginKind.Settings, "home-row")
            .Should()
            .BeFalse("a slot is known for a kind, not on its own");
    }

    [Fact]
    public void A_placement_names_a_kind_a_slot_and_a_structured_route()
    {
        PluginPlacement placement = new(
            PluginKind.Music,
            "home-row",
            "radio.row.favorites",
            "portableRadio",
            new("favorites"),
            []
        );

        placement.Route.Route.Should().Be("favorites");
        placement
            .Surfaces.Should()
            .BeEmpty("empty means every surface, so a plugin appears on a television by default");
    }

    [Fact]
    public void A_nav_entry_that_says_nothing_is_a_shared_navigation_button()
    {
        PluginNavEntry entry = new()
        {
            Section = PluginUiSection.Music,
            Label = "stations.title",
            Route = "/",
        };

        entry
            .Slot.Should()
            .Be(PluginSlot.Nav, "a plugin that says nothing lands where it always did");
        entry.Access.Should().Be(PluginRouteAccess.Shared);
    }

    [Fact]
    public void A_mount_that_says_nothing_is_a_shared_navigation_button()
    {
        PluginUiMount mount = new()
        {
            Section = PluginUiSection.Dashboard,
            Label = "settings.title",
            Route = "/",
        };

        mount.Slot.Should().Be(PluginSlot.Nav);
        mount.Access.Should().Be(PluginRouteAccess.Shared);
    }

    [Fact]
    public void An_owner_route_is_declared_on_the_entry_rather_than_guessed()
    {
        PluginNavEntry entry = new()
        {
            Section = PluginUiSection.Music,
            Label = "stations.manage",
            Route = "/manage",
            Slot = PluginSlot.HomeRow,
            Access = PluginRouteAccess.Owner,
        };

        entry.Slot.Should().Be("home-row");
        entry.Access.Should().Be(PluginRouteAccess.Owner);
    }

    [Fact]
    public void A_route_reference_carries_parameters_rather_than_a_joined_string()
    {
        PluginRouteRef reference = new(
            "genre",
            new Dictionary<string, string> { ["name"] = "ambient, chill" }
        );

        reference.Params["name"].Should().Be("ambient, chill");
        reference
            .Route.Should()
            .NotContain(",", "a value with a separator in it would become two segments");
    }

    [Fact]
    public void A_route_that_starts_with_an_underscore_refuses()
    {
        Action declare = () =>
            new PluginRouteTable(
                new PluginRoute
                {
                    Path = "/_info",
                    Name = "info",
                    Label = "x",
                }
            );

        declare
            .Should()
            .Throw<PluginRefusedException>()
            .Which.Refusal.Code.Should()
            .Be(PluginRefusalCodes.RouteReservedPrefix);
    }

    [Fact]
    public void An_ordinary_route_is_still_accepted()
    {
        Action declare = () =>
            new PluginRouteTable(
                new PluginRoute
                {
                    Path = "/info",
                    Name = "info",
                    Label = "x",
                }
            );

        declare.Should().NotThrow("the guard must catch the underscore, not every route");
    }

    [Fact]
    public void A_media_card_carries_the_id_the_client_needs_to_play_it()
    {
        PluginMediaCard card = new()
        {
            Media = MediaId.Parse("01J9ZK5V8Y0000000000000000"),
            Title = "Radio 538",
            Artist = "Now playing",
            Cover = new("https://cdn.example/538.png"),
        };

        card.Media.Should().Be(MediaId.Parse("01J9ZK5V8Y0000000000000000"));
        card.Artist.Should().Be("Now playing");
        card.ChannelId.Should().BeNull("a card is library media or a channel, never both");
    }

    [Fact]
    public void A_card_for_a_live_channel_names_the_channel_instead()
    {
        PluginMediaCard card = new() { ChannelId = "npo1", Title = "NPO 1" };

        card.ChannelId.Should().Be("npo1");
        card.Media.Should().BeNull();
    }

    [Fact]
    public void An_overlay_expires_so_it_cannot_outlive_what_it_described()
    {
        PluginOverlay overlay = new()
        {
            TitleKey = "radio.overlay.nowplaying",
            Duration = TimeSpan.FromSeconds(8),
        };

        overlay.Duration.Should().Be(TimeSpan.FromSeconds(8));
        typeof(PluginOverlay)
            .GetProperty("Duration")!
            .PropertyType.Should()
            .Be(typeof(TimeSpan), "a nullable duration would let a plugin ask for forever");
    }
}
