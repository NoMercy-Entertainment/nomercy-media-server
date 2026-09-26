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

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Where the publisher keys come from: the keys this build shipped with, then
/// what nomercy.tv publishes, then what the owner configured. A server with no
/// network keeps the shipped keys and every part of it keeps working.
/// </summary>
public class PluginMarketplaceKeysTests
{
    private const string Active = "UbpdyF8xAjmWCWF0YWEOhfXO6OIUBcr39VsNw8jMiKQ=";

    private static readonly IReadOnlyDictionary<string, string> NoConfig =
        new Dictionary<string, string>();

    [Fact]
    public void The_shipped_keys_are_the_ones_nomercy_tv_publishes()
    {
        PluginMarketplaceKeys keys = new(NoConfig);

        keys.Any.Should().BeTrue();
        keys.Find("mk_2026_09c").Should().Be(Active);
        keys.Find("mk_2026_09b").Should().Be("XY/C4NlknNsxIjkhXMKtv3amk6kqlrGYe/kTc0uU/ro=");
        keys.Find("mk_2026_09").Should().Be("8llIlaK+E9+/rLiK246qcOWckL0m0Rs+F65SbPIaDu4=");
    }

    [Fact]
    public void A_published_key_is_added_and_extra_fields_are_ignored()
    {
        PluginMarketplaceKeys keys = new(NoConfig);

        keys.Apply(
                """{"keys":[{"kid":"mk_2026_10","alg":"Ed25519","public_key":"TkVX","active":true,"note":"new"}],"extra":1}"""
            )
            .Should()
            .BeTrue();

        keys.Find("mk_2026_10").Should().Be("TkVX");
        keys.Find("mk_2026_09c")
            .Should()
            .Be(Active, "a key set that omits a kid does not revoke it");
    }

    [Fact]
    public void A_key_marked_revoked_is_dropped_even_when_it_shipped_with_the_build()
    {
        PluginMarketplaceKeys keys = new(NoConfig);

        keys.Apply(
            """{"keys":[{"kid":"mk_2026_09","alg":"Ed25519","public_key":"8llIlaK+E9+/rLiK246qcOWckL0m0Rs+F65SbPIaDu4=","active":false,"revoked":true}]}"""
        );

        keys.Find("mk_2026_09").Should().BeNull();
        keys.Find("mk_2026_09c").Should().Be(Active);
    }

    [Fact]
    public void The_owners_configured_keys_are_merged_on_top()
    {
        PluginMarketplaceKeys keys = new(
            new Dictionary<string, string> { ["own"] = "T1dO", ["mk_2026_09c"] = "T1ZFUg==" }
        );

        keys.Find("own").Should().Be("T1dO");
        keys.Find("mk_2026_09c").Should().Be("T1ZFUg==");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"keys":"nope"}""")]
    [InlineData("""{"other":[]}""")]
    public void An_answer_that_is_not_a_key_set_changes_nothing(string body)
    {
        PluginMarketplaceKeys keys = new(NoConfig);

        keys.Apply(body).Should().BeFalse();
        keys.Find("mk_2026_09c").Should().Be(Active);
    }

    [Fact]
    public async Task A_fetch_that_fails_keeps_the_keys_and_logs_once()
    {
        PluginMarketplaceKeys keys = new(NoConfig);
        ListLogger logger = new();
        PluginMarketplaceKeyClient client = new(
            new HttpClient(new StatusHandler(HttpStatusCode.BadGateway)),
            keys,
            new ListLoggerOf<PluginMarketplaceKeyClient>(logger)
        );

        await client.RefreshAsync(new("https://api.nomercy.tv/v1/marketplace/keys.json"));
        await client.RefreshAsync(new("https://api.nomercy.tv/v1/marketplace/keys.json"));

        keys.Find("mk_2026_09c").Should().Be(Active);
        logger
            .Entries.Where(entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning)
            .Should()
            .HaveCount(1);
    }

    [Fact]
    public async Task A_fetch_that_answers_adds_the_published_keys()
    {
        PluginMarketplaceKeys keys = new(NoConfig);
        PluginMarketplaceKeyClient client = new(
            new HttpClient(
                new BodyHandler(
                    """{"keys":[{"kid":"mk_2026_10","alg":"Ed25519","public_key":"TkVX","active":true}]}"""
                )
            ),
            keys,
            NullLogger<PluginMarketplaceKeyClient>.Instance
        );

        await client.RefreshAsync(new("https://api.nomercy.tv/v1/marketplace/keys.json"));

        keys.Find("mk_2026_10").Should().Be("TkVX");
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class BodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
            );
    }
}
