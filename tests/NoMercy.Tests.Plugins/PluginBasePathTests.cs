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
/// The server owns eight pages for every plugin, and a plugin cannot claim
/// their prefix. A plugin that could would be drawing the page that says what
/// it is allowed to do.
/// </summary>
public class PluginBasePathTests
{
    [Fact]
    public void A_plugin_route_may_not_start_with_the_host_prefix()
    {
        Action claiming = () => _ = new PluginRoute { Path = "/_/info", Name = "sneaky" };

        claiming
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*reserved for the server's own pages*");
    }

    [Fact]
    public void The_refusal_says_what_to_do_instead()
    {
        Action claiming = () => _ = new PluginRoute { Path = "/_/health", Name = "sneaky" };

        claiming.Should().Throw<ArgumentException>().WithMessage("*/overview*");
    }

    [Fact]
    public void A_route_that_merely_contains_an_underscore_is_fine()
    {
        Action ordinary = () => _ = new PluginRoute { Path = "/now_playing", Name = "now" };

        ordinary.Should().NotThrow();
    }

    [Fact]
    public void A_route_that_starts_with_an_underscore_word_is_fine()
    {
        Action ordinary = () => _ = new PluginRoute { Path = "/_hidden", Name = "hidden" };

        ordinary
            .Should()
            .NotThrow("the prefix is the whole first segment, not the first character");
    }

    [Fact]
    public void The_prefix_is_reserved_with_or_without_a_leading_slash()
    {
        PluginBasePath.IsReserved("_/info").Should().BeTrue();
        PluginBasePath.IsReserved("/_/info").Should().BeTrue();
    }

    [Fact]
    public void The_host_pages_are_the_eight_design_names()
    {
        PluginBasePath
            .Pages.Should()
            .BeEquivalentTo([
                "info",
                "permissions",
                "settings",
                "update",
                "remove",
                "docs",
                "license",
                "health",
            ]);
    }

    [Fact]
    public void Only_what_changes_the_server_is_kept_to_the_owner()
    {
        PluginBasePath
            .OwnerOnly.Should()
            .BeEquivalentTo(["permissions", "update", "remove", "health"]);
    }

    [Fact]
    public void Every_owner_only_page_is_one_of_the_pages()
    {
        PluginBasePath.OwnerOnly.Should().BeSubsetOf(PluginBasePath.Pages);
    }
}
