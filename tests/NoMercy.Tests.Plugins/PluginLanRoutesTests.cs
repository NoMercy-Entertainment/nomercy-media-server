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
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// What a manifest may name as a route for a device that cannot sign in. The
/// rules leave no way to write a catch-all, an encoded character or a step
/// out of the plugin's prefix, so the route the owner read is the only thing
/// a device can reach.
/// </summary>
public class PluginLanRoutesTests
{
    private const string Manifest = """
        {
          "id": "01J9ZK5V8Y0000000000000010",
          "name": "Live TV",
          "description": "Channels.",
          "version": "1.0.0",
          "targetAbi": "12.10",
          "assembly": "LiveTv.dll",
          "capabilities": {
            "rest": true,
            "lan": { "routes": ["hdhr/discover.json", "hdhr/lineup.json", "hdhr/auto/{channel}"] }
          }
        }
        """;

    [Theory]
    [InlineData("hdhr/discover.json")]
    [InlineData("hdhr/lineup_status.json")]
    [InlineData("hdhr/auto/{channel}")]
    [InlineData("discover.json")]
    [InlineData("a-b/c~d/{value_1}")]
    public void A_plain_route_is_accepted(string route)
    {
        PluginLanRoutes.Problem(route).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/hdhr/discover.json")]
    [InlineData("hdhr/discover.json/")]
    [InlineData("hdhr//discover.json")]
    [InlineData("../movies")]
    [InlineData("hdhr/../../movies")]
    [InlineData("./discover.json")]
    [InlineData("%2e%2e/movies")]
    [InlineData("hdhr%2Fdiscover.json")]
    [InlineData("hdhr\\discover.json")]
    [InlineData("hdhr/discover.json?x=1")]
    [InlineData("hdhr/{**rest}")]
    [InlineData("hdhr/{*rest}")]
    [InlineData("hdhr/{channel?}")]
    [InlineData("hdhr/{channel:int}")]
    [InlineData("hdhr/{channel=1}")]
    [InlineData("hdhr/v{channel}")]
    [InlineData("hdhr/{1channel}")]
    [InlineData("hdhr/{}")]
    [InlineData("{a}/{a}")]
    [InlineData("hdhr/disc over.json")]
    public void A_route_that_could_reach_more_than_it_says_is_refused(string route)
    {
        PluginLanRoutes.Problem(route).Should().NotBeNull();
    }

    [Fact]
    public void Routes_without_rest_are_refused_because_they_are_rest_endpoints()
    {
        PluginCapabilities capabilities = new() { Lan = new() { Routes = ["hdhr/discover.json"] } };

        PluginLanRoutes.Problems(capabilities).Should().ContainSingle();
    }

    [Fact]
    public void More_routes_than_a_tuner_needs_are_refused()
    {
        PluginCapabilities capabilities = new()
        {
            Rest = true,
            Lan = new()
            {
                Routes =
                [
                    .. Enumerable.Range(0, PluginLanRoutes.MaxRoutes + 1).Select(i => $"r{i}"),
                ],
            },
        };

        PluginLanRoutes.Problems(capabilities).Should().ContainSingle();
    }

    [Fact]
    public void A_route_named_twice_is_refused()
    {
        PluginCapabilities capabilities = new()
        {
            Rest = true,
            Lan = new() { Routes = ["hdhr/lineup.json", "HDHR/lineup.json"] },
        };

        PluginLanRoutes.Problems(capabilities).Should().ContainSingle();
    }

    [Fact]
    public void A_plugin_that_names_no_lan_routes_has_nothing_to_check()
    {
        PluginLanRoutes.Problems(null).Should().BeEmpty();
        PluginLanRoutes.Problems(new() { Rest = true }).Should().BeEmpty();
        PluginLanRoutes.Problems(new() { Lan = new() }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("5.1", true)]
    [InlineData("v5.1", true)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("%2e%2e", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void A_value_a_device_sends_is_plain_text_or_nothing(string value, bool plain)
    {
        PluginLanRoutes.IsPlainSegment(value).Should().Be(plain);
    }

    [Fact]
    public void The_manifest_reads_the_routes_it_names()
    {
        PluginManifest manifest = PluginManifestParser.Parse(Manifest);

        manifest
            .Capabilities!.Lan!.Routes.Should()
            .Equal("hdhr/discover.json", "hdhr/lineup.json", "hdhr/auto/{channel}");
    }

    [Fact]
    public void A_manifest_with_a_route_the_server_cannot_serve_does_not_load()
    {
        Action parse = () =>
            PluginManifestParser.Parse(Manifest.Replace("hdhr/lineup.json", "hdhr/{**rest}"));

        parse.Should().Throw<InvalidOperationException>().WithMessage("*hdhr/{**rest}*");
    }

    [Fact]
    public void A_manifest_from_before_lan_routes_loads_as_it_did()
    {
        PluginManifest manifest = PluginManifestParser.Parse(
            """
            {
              "id": "01J9ZK5V8Y0000000000000010",
              "name": "Live TV",
              "description": "Channels.",
              "version": "1.0.0",
              "targetAbi": "12.9",
              "assembly": "LiveTv.dll",
              "capabilities": { "rest": true }
            }
            """
        );

        manifest.Capabilities!.Lan.Should().BeNull();
    }

    [Fact]
    public void A_new_lan_route_asks_the_owner_again()
    {
        PluginCapabilities consented = new()
        {
            Rest = true,
            Lan = new() { Routes = ["hdhr/discover.json"] },
        };
        PluginCapabilities current = new()
        {
            Rest = true,
            Lan = new() { Routes = ["hdhr/discover.json", "hdhr/lineup.json"] },
        };

        PluginCapabilityGuard.HasWidened(consented, current).Should().BeTrue();
    }

    [Fact]
    public void Lan_routes_on_a_consent_that_had_none_ask_the_owner_again()
    {
        PluginCapabilityGuard
            .HasWidened(
                new() { Rest = true },
                new()
                {
                    Rest = true,
                    Lan = new() { Routes = ["hdhr/discover.json"] },
                }
            )
            .Should()
            .BeTrue();
    }

    [Fact]
    public void Dropping_a_lan_route_does_not_ask_again()
    {
        PluginCapabilities consented = new()
        {
            Rest = true,
            Lan = new() { Routes = ["hdhr/discover.json", "hdhr/lineup.json"] },
        };
        PluginCapabilities current = new()
        {
            Rest = true,
            Lan = new() { Routes = ["hdhr/discover.json"] },
        };

        PluginCapabilityGuard.HasWidened(consented, current).Should().BeFalse();
    }
}
