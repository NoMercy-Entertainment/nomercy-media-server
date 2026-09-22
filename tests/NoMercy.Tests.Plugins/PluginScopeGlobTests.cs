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
using NoMercy.PluginSdk.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginScopeGlobTests
{
    [Theory]
    [InlineData("api.example.com", "api.example.com", true)]
    [InlineData("api.example.com", "other.example.com", false)]
    [InlineData("*.example.com", "api.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com", false)]
    [InlineData("**.example.com", "a.b.example.com", true)]
    [InlineData("**", "anything.at.all", true)]
    [InlineData("*", "nodots", true)]
    [InlineData("*", "has.dots", false)]
    public void One_star_stays_in_a_segment_and_two_cross_separators(
        string scope,
        string value,
        bool expected
    )
    {
        PluginScopeGlob.Matches(scope, value).Should().Be(expected);
    }

    [Fact]
    public void Matching_ignores_case_because_a_hostname_does()
    {
        PluginScopeGlob.Matches("*.Example.COM", "api.example.com").Should().BeTrue();
    }

    [Fact]
    public void A_dot_in_the_scope_is_a_dot_and_not_any_character()
    {
        PluginScopeGlob
            .Matches("apiXexample.com", "api.example.com")
            .Should()
            .BeFalse("an unescaped dot would let one host stand in for another");
    }

    [Fact]
    public void A_scope_matches_only_the_whole_value()
    {
        PluginScopeGlob
            .Matches("example.com", "evil-example.com.attacker.test")
            .Should()
            .BeFalse("an unanchored pattern lets an attacker append their own domain");
        PluginScopeGlob.Matches("example.com", "example.com.attacker.test").Should().BeFalse();
    }

    [Fact]
    public void The_separator_is_the_callers_because_a_path_is_not_a_hostname()
    {
        PluginScopeGlob.Matches("media/*", "media/movies", '/').Should().BeTrue();
        PluginScopeGlob
            .Matches("media/*", "media/movies/action", '/')
            .Should()
            .BeFalse("one star must stop at a path separator the same way it stops at a dot");
        PluginScopeGlob.Matches("media/**", "media/movies/action", '/').Should().BeTrue();
    }

    [Fact]
    public void Any_matches_answers_for_a_list()
    {
        string[] scopes = ["a.example.com", "*.other.example"];

        PluginScopeGlob.AnyMatches(scopes, "api.other.example").Should().BeTrue();
        PluginScopeGlob.AnyMatches(scopes, "nothing.test").Should().BeFalse();
        PluginScopeGlob.AnyMatches([], "anything").Should().BeFalse();
    }
}
