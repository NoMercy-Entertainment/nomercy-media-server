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

using System.Security.Claims;
using FluentAssertions;
using NoMercy.Api.Plugins;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// One way to build the caller a plugin sees, so a page, a search and a hub
/// message all describe the same person the same way.
/// </summary>
[Trait("Category", "Unit")]
public class PluginCallersTests
{
    private static readonly Guid Person = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static ClaimsPrincipal User(string role) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Person.ToString()),
                    new Claim(ClaimTypes.Role, role),
                    new Claim("name", "Sam"),
                ],
                "TestAuth"
            )
        );

    [Fact]
    public void The_owner_is_an_owner_with_the_access_and_language_given()
    {
        PluginCaller caller = PluginCallers.For(
            User("owner"),
            PluginAccess.Owned,
            "nl-NL",
            PluginSurface.Web
        );

        caller.Id.Should().Be(new UserId(new Ulid(Person)));
        caller.DisplayName.Should().Be("Sam");
        caller.Role.Should().Be(PluginRole.Owner);
        caller.Access.Should().Be(PluginAccess.Owned);
        caller.Locale.Should().Be("nl-NL");
    }

    [Fact]
    public void Anybody_else_is_a_member()
    {
        PluginCallers
            .For(User("user"), PluginAccess.Shared, null, null)
            .Role.Should()
            .Be(PluginRole.Member);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_language_the_caller_reads_english(string? language)
    {
        PluginCallers
            .For(User("owner"), PluginAccess.Owned, language, null)
            .Locale.Should()
            .Be("en");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("toaster")]
    public void An_unknown_surface_falls_back_to_web(string? surface)
    {
        PluginCallers
            .For(User("owner"), PluginAccess.Owned, null, surface)
            .Surface.Should()
            .Be(PluginSurface.Web);
    }
}
