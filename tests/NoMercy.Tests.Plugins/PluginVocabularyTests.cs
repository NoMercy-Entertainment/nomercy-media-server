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

/// <summary>
/// The five words every client has to agree with the server on.
///
/// Each of these is read by tools/nm-components and written into TypeScript,
/// Kotlin and Swift. A value that changes here changes on every client; a
/// value a client spells for itself does not, and that is the whole failure
/// this vocabulary exists to stop.
/// </summary>
public class PluginVocabularyTests
{
    [Fact]
    public void Access_has_exactly_three_answers()
    {
        Enum.GetValues<PluginAccess>()
            .Should()
            .Equal(PluginAccess.Owned, PluginAccess.Shared, PluginAccess.None);
    }

    [Fact]
    public void Slots_are_listed_per_kind()
    {
        PluginSlot
            .For(PluginKind.Video)
            .Should()
            .Equal(
                "nav",
                "home-row",
                "detail-tab",
                "player-panel",
                "live",
                "guide",
                "channel-strip"
            );

        PluginSlot.For(PluginKind.Library).Should().Equal("nav", "home-row", "library-card");
        PluginSlot.For(PluginKind.Dashboard).Should().Equal("nav", "card");
        PluginSlot.For(PluginKind.Addon).Should().Equal("nav", "home");
    }

    [Fact]
    public void A_slot_is_valid_for_a_kind_rather_than_on_its_own()
    {
        PluginSlot.IsValid(PluginKind.Music, "home-row").Should().BeTrue();
        PluginSlot.IsValid(PluginKind.Settings, "home-row").Should().BeFalse();
        PluginSlot.IsValid("nothing-at-all", "nav").Should().BeFalse();
    }

    [Fact]
    public void Every_slot_the_table_names_is_in_the_vocabulary()
    {
        PluginSlot
            .All.Should()
            .BeEquivalentTo(
                PluginSlots.All.Select(slot => slot.Slot).Distinct(),
                "the words and the kind table are generated from one list"
            );
    }

    [Fact]
    public void Base_path_pages_cover_the_whole_management_area()
    {
        PluginBasePathPage
            .All.Should()
            .Equal(
                "info",
                "permissions",
                "settings",
                "update",
                "remove",
                "docs",
                "license",
                "health"
            );
    }

    [Fact]
    public void Four_pages_are_the_owners_alone()
    {
        PluginBasePathPage.OwnerOnly.Should().Equal("permissions", "update", "remove", "health");

        PluginBasePathPage
            .All.Except(PluginBasePathPage.OwnerOnly)
            .Should()
            .Equal("info", "settings", "docs", "license");
    }

    [Fact]
    public void The_base_path_reads_its_pages_from_the_vocabulary()
    {
        PluginBasePath.Pages.Should().Equal(PluginBasePathPage.All);
        PluginBasePath.OwnerOnly.Should().Equal(PluginBasePathPage.OwnerOnly);
    }

    [Fact]
    public void A_route_starting_with_underscore_is_reserved()
    {
        PluginBasePath.IsReserved("_/info").Should().BeTrue();
        PluginBasePath.IsReserved("stations").Should().BeFalse();
    }

    [Fact]
    public void An_unknown_base_path_page_is_not_known()
    {
        PluginBasePathPage.IsKnown("permissions").Should().BeTrue();
        PluginBasePathPage.IsKnown("billing").Should().BeFalse();
        PluginBasePathPage.IsKnown(null).Should().BeFalse();
    }

    [Fact]
    public void Input_devices_name_the_three_ways_a_surface_is_driven()
    {
        PluginInputDevice.All.Should().Equal("pointer", "touch", "remote");
        PluginInputDevice.IsKnown("remote").Should().BeTrue();
        PluginInputDevice.IsKnown("keyboard").Should().BeFalse();
    }

    [Fact]
    public void Live_actions_name_every_button_the_host_draws()
    {
        PluginLiveAction
            .All.Should()
            .Equal(
                "channel-up",
                "channel-down",
                "channel-number",
                "last-channel",
                "mini-guide",
                "jump-to-live",
                "start-over",
                "record",
                "quality"
            );

        PluginLiveAction.IsKnown("start-over").Should().BeTrue();
        PluginLiveAction.IsKnown("rewind").Should().BeFalse();
    }
}
