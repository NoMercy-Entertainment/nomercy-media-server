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
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.Dtos;

/// <summary>
/// A navigation entry says where it draws and who may open it.
///
/// Without the slot every placement was a navigation button, and without the
/// access a member was offered owner pages that answer 403 when opened.
/// </summary>
public class PluginNavEntryDtoTests
{
    [Fact]
    public void An_entry_that_says_nothing_is_a_shared_navigation_button()
    {
        PluginNavEntryDto entry = PluginNavEntryDto.From(
            new PluginNavEntry
            {
                Section = PluginUiSection.Music,
                Label = "stations.title",
                Route = "/",
            }
        );

        entry.Slot.Should().Be("nav", "a plugin that says nothing lands where it always did");
        entry.Access.Should().Be("shared");
    }

    [Fact]
    public void An_entry_carries_the_slot_and_the_access_it_declared()
    {
        PluginNavEntryDto entry = PluginNavEntryDto.From(
            new PluginNavEntry
            {
                Section = PluginUiSection.Music,
                Label = "stations.title",
                Route = "/favorites",
                Slot = PluginSlot.HomeRow,
                Access = PluginRouteAccess.Owner,
            }
        );

        entry.Slot.Should().Be("home-row");
        entry.Access.Should().Be("owner");
    }

    [Fact]
    public void A_mount_carries_the_same_two_answers()
    {
        PluginNavEntryDto entry = PluginNavEntryDto.From(
            new PluginUiMount
            {
                Section = PluginUiSection.Video,
                Label = "guide.title",
                Route = "/guide",
                Slot = PluginSlot.Guide,
                Access = PluginRouteAccess.Owner,
            }
        );

        entry.Slot.Should().Be("guide");
        entry.Access.Should().Be("owner");
    }

    [Fact]
    public void A_mount_that_says_nothing_is_a_shared_navigation_button()
    {
        PluginNavEntryDto entry = PluginNavEntryDto.From(
            new PluginUiMount
            {
                Section = PluginUiSection.Dashboard,
                Label = "settings.title",
                Route = "/",
            }
        );

        entry.Slot.Should().Be("nav");
        entry.Access.Should().Be("shared");
    }
}
