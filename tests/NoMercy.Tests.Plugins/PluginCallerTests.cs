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

public class PluginCallerTests
{
    private static PluginCaller Caller(PluginRole role)
    {
        return new PluginCaller(
            UserId.Parse("01J9ZK5V8Y0000000000000000"),
            "Stoney",
            role,
            PluginAccess.Owned,
            "en",
            "tv"
        );
    }

    [Fact]
    public void A_view_request_carries_the_caller()
    {
        PluginViewRequest request = new()
        {
            Route = "/",
            Surface = "tv",
            Caller = Caller(PluginRole.Member),
        };

        request.Caller.Role.Should().Be(PluginRole.Member);
        request.Caller.Locale.Should().Be("en");
    }

    [Theory]
    [InlineData(PluginRole.Owner, true)]
    [InlineData(PluginRole.Manager, true)]
    [InlineData(PluginRole.Member, false)]
    [InlineData(PluginRole.Guest, false)]
    public void An_owner_route_admits_only_the_owner_and_a_manager(PluginRole role, bool admitted)
    {
        PluginCaller caller = Caller(role);

        caller.Admits(PluginRouteAccess.Owner).Should().Be(admitted);
    }

    [Theory]
    [InlineData(PluginRole.Owner)]
    [InlineData(PluginRole.Member)]
    [InlineData(PluginRole.Guest)]
    public void A_shared_route_admits_everyone_who_reached_the_plugin(PluginRole role)
    {
        Caller(role).Admits(PluginRouteAccess.Shared).Should().BeTrue();
    }

    [Fact]
    public void A_route_defaults_to_shared()
    {
        new PluginRoute
        {
            Path = "/",
            Name = "home",
            Label = "radio.nav",
        }
            .Access.Should()
            .Be(PluginRouteAccess.Shared);
    }

    [Fact]
    public void A_refused_route_names_the_code()
    {
        PluginRefusal refusal = PluginCaller.RefuseRoute(
            Caller(PluginRole.Member),
            "/settings",
            "Internet Radio 2.0.0"
        );

        refusal.Code.Should().Be(PluginRefusalCodes.RouteAccessDenied);
        refusal.Severity.Should().Be(PluginRefusalSeverity.Blocked);
    }
}
