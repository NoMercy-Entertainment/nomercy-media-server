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

using System.Reflection;
using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginMediaProxyContractTests
{
    [Fact]
    public void A_proxy_request_carries_prioritized_links_with_their_own_headers()
    {
        PluginProxyRequest request = new()
        {
            Links =
            [
                new()
                {
                    Url = new("https://one.example/stream.m3u8"),
                    Referer = "https://one.example/",
                    UserAgent = "NoMercy",
                },
                new() { Url = new("https://two.example/stream.m3u8") },
            ],
        };

        request.Links.Should().HaveCount(2);
        request.Links[0].Referer.Should().Be("https://one.example/");
        request
            .Links[1]
            .Referer.Should()
            .BeNull("a second upstream need not share the first one's headers");
    }

    [Fact]
    public void A_proxy_request_passes_range_rewrites_playlists_and_follows_redirects_by_default()
    {
        PluginProxyRequest request = new() { Links = [] };

        request.PassRange.Should().BeTrue();
        request.RewriteHlsPlaylists.Should().BeTrue();
        request.FollowRedirects.Should().BeTrue();
        request
            .Timeout.Should()
            .BeNull("a live stream that is cut off at a deadline is a bug, not a safeguard");
    }

    [Fact]
    public void The_url_is_minted_by_the_host_and_has_no_settable_token()
    {
        PropertyInfo[] properties = typeof(PluginMediaUrl).GetProperties();

        properties
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo(["Url", "ExpiresAt", "Media"]);
        properties
            .Should()
            .OnlyContain(property => property.SetMethod == null || property.SetMethod.IsAssembly);
    }

    [Fact]
    public void Putting_a_token_in_a_url_refuses_and_names_the_proxy()
    {
        PluginRefusal refusal = PluginRefusalMessages.TokenInUrl(
            "Internet Radio 1.2.1",
            "/api/v1/plugins/5KTKRT4Z2Y9P59Y40W5CX4TQKF/stream/42?access_token=..."
        );

        refusal.Code.Should().Be(PluginRefusalCodes.TokenInUrl);
        refusal.Fix.Should().Contain("context.Media.Proxy");
        refusal.Fix.Should().Contain("/nomercy-plugins/capabilities/media-proxy");
    }

    [Fact]
    public void A_remux_request_names_the_container_not_a_command_line()
    {
        PluginRemuxRequest request = new()
        {
            Source = new("https://one.example/stream.ts"),
            Container = PluginRemuxContainer.Hls,
        };

        request.Container.Should().Be(PluginRemuxContainer.Hls);
        Enum.GetNames<PluginRemuxContainer>().Should().BeEquivalentTo(["Hls", "Mp4"]);
    }

    [Fact]
    public void A_link_resolves_its_credentials_server_side_only()
    {
        PropertyInfo resolve = typeof(PluginProxyLink).GetProperty("ResolveAsync")!;

        resolve.PropertyType.Should().Be(typeof(Func<CancellationToken, Task<Uri>>));
    }
}
