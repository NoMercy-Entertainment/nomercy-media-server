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
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginRouteChromeTests
{
    [Fact]
    public void A_route_that_says_nothing_keeps_the_apps_navbar()
    {
        PluginRoute route = new() { Path = "/info", Name = "info" };

        route.Chrome.Should().Be(PluginChrome.App);
    }

    [Fact]
    public void A_route_can_take_the_whole_screen()
    {
        PluginRoute route = new()
        {
            Path = "/player",
            Name = "player",
            Chrome = PluginChrome.None,
        };

        route.Chrome.Should().Be("none");
    }

    [Fact]
    public void A_route_with_an_unknown_chrome_refuses_with_its_code()
    {
        Action declare = () =>
            new PluginRouteTable(
                new PluginRoute
                {
                    Path = "/player",
                    Name = "player",
                    Chrome = "fullscreen",
                }
            );

        PluginRefusal refusal = declare.Should().Throw<PluginRefusedException>().Which.Refusal;

        refusal.Code.Should().Be(PluginRefusalCodes.RouteChromeUnknown);
        refusal.Code.Should().Be("PLUGIN_ROUTE_CHROME_UNKNOWN");
        refusal.What.Should().Contain("fullscreen");
    }

    [Fact]
    public void Both_known_values_are_accepted_by_the_table()
    {
        Action declare = () =>
            new PluginRouteTable(
                new PluginRoute
                {
                    Path = "/a",
                    Name = "a",
                    Chrome = PluginChrome.App,
                },
                new PluginRoute
                {
                    Path = "/b",
                    Name = "b",
                    Chrome = PluginChrome.None,
                }
            );

        declare.Should().NotThrow();
    }

    [Fact]
    public void The_refusal_code_is_in_the_registry()
    {
        PluginRefusalCodes
            .ByCode("PLUGIN_ROUTE_CHROME_UNKNOWN")
            .Should()
            .NotBeNull("a code nobody can look up is a refusal the author cannot read");
    }
}
