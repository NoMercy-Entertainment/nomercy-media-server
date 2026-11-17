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
using NoMercy.Api.Plugins;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// How the path a device asks for is matched against a route the manifest
/// names: segment for segment, plain text only, nothing that steps or spans.
/// </summary>
[Trait("Category", "Unit")]
public class PluginLanRouteTemplateTests
{
    [Theory]
    [InlineData("hdhr/discover.json", "hdhr/discover.json")]
    [InlineData("hdhr/discover.json", "HDHR/Discover.json")]
    [InlineData("hdhr/auto/{channel}", "hdhr/auto/v5.1")]
    public void A_path_that_is_the_route_matches(string route, string path)
    {
        PluginLanRouteTemplate.Match(route, path).Should().NotBeNull();
    }

    [Fact]
    public void A_named_value_is_filled_in_from_its_one_segment()
    {
        PluginLanRouteTemplate
            .Match("hdhr/auto/{channel}", "hdhr/auto/v5.1")!
            .Should()
            .ContainKey("channel")
            .WhoseValue.Should()
            .Be("v5.1");
    }

    [Theory]
    [InlineData("hdhr/discover.json", "hdhr/lineup.json")]
    [InlineData("hdhr/discover.json", "hdhr/discover.json/")]
    [InlineData("hdhr/discover.json", "hdhr//discover.json")]
    [InlineData("hdhr/discover.json", "x/../hdhr/discover.json")]
    [InlineData("hdhr/auto/{channel}", "hdhr/auto/..")]
    [InlineData("hdhr/auto/{channel}", "hdhr/auto/.")]
    [InlineData("hdhr/auto/{channel}", "hdhr/auto/%2e%2e")]
    [InlineData("hdhr/auto/{channel}", "hdhr/auto/a%2Fb")]
    [InlineData("hdhr/auto/{channel}", "hdhr/auto/a/b")]
    [InlineData("hdhr/auto/{channel}", "hdhr/auto/")]
    [InlineData("hdhr/auto/{channel}", "hdhr/auto/a b")]
    [InlineData("{channel}", "..")]
    public void A_path_that_steps_spans_or_encodes_matches_nothing(string route, string path)
    {
        PluginLanRouteTemplate.Match(route, path).Should().BeNull();
    }

    [Theory]
    [InlineData("/api/v1/plugins/x/lan/d/c/hdhr/lineup.json", true)]
    [InlineData("/api/v1/plugins/x/lan/d/c/hdhr/lineup.json?x=%2e%2e", true)]
    [InlineData(null, true)]
    [InlineData("/api/v1/plugins/x/lan/d/c/hdhr/../hdhr/lineup.json", false)]
    [InlineData("/api/v1/plugins/x/lan/d/c/hdhr/./lineup.json", false)]
    [InlineData("/api/v1/plugins/x/lan/d/c/hdhr/%2e%2e/lineup.json", false)]
    [InlineData("/api/v1/plugins/x/lan/d/c/hdhr%2Flineup.json", false)]
    [InlineData("/api/v1/plugins/x/lan/d/c/hdhr//lineup.json", false)]
    [InlineData("/api/v1/plugins/x/lan/d/c/hdhr/lineup.json/", false)]
    [InlineData("api/v1/plugins/x/lan/d/c/hdhr/lineup.json", false)]
    [InlineData("http://elsewhere/api/v1/plugins/x/lan/d/c/hdhr/lineup.json", false)]
    public void The_address_as_sent_must_be_plain(string? rawTarget, bool plain)
    {
        PluginLanRequest.IsPlainRawTarget(rawTarget).Should().Be(plain);
    }

    [Fact]
    public void The_route_with_more_plain_text_is_the_more_specific()
    {
        PluginLanRouteTemplate
            .Specificity("hdhr/lineup.json")
            .Should()
            .BeGreaterThan(PluginLanRouteTemplate.Specificity("hdhr/{file}"));
    }
}
