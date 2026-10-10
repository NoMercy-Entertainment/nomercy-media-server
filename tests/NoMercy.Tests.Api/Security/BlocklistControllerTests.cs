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
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Api.Security;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Security;

public class BlocklistControllerTests(NoMercyApiFactory factory) : IClassFixture<NoMercyApiFactory>
{
    [Fact]
    public async Task Feed_WithoutAToken_Is404()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/security/blocklist/");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Feed_WithTheWrongToken_Is404AndRevealsNothing()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/security/blocklist/not-the-token");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotContain("203.0.113");
    }

    [Fact]
    public async Task Feed_WithTheRightToken_ReturnsOneAddressPerLineAsPlainText()
    {
        IBlocklistFeedSettings feed = factory.Services.GetRequiredService<IBlocklistFeedSettings>();
        IAbuseGuard guard = factory.Services.GetRequiredService<IAbuseGuard>();
        string token = await feed.EnsureTokenAsync(CancellationToken.None);
        await guard.BanAsync(
            IPAddress.Parse("203.0.113.210"),
            "KnownProbe",
            TimeSpan.FromHours(1),
            false,
            CancellationToken.None
        );
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/security/blocklist/{token}");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().Contain("203.0.113.210");
    }

    [Fact]
    public async Task RotateToken_InvalidatesThePreviousUrl()
    {
        IBlocklistFeedSettings feed = factory.Services.GetRequiredService<IBlocklistFeedSettings>();

        string first = await feed.EnsureTokenAsync(CancellationToken.None);
        string second = await feed.RotateTokenAsync(CancellationToken.None);

        second.Should().NotBe(first);
        (await feed.VerifyAsync(first, CancellationToken.None)).Should().BeFalse();
        (await feed.VerifyAsync(second, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task BlocklistUrl_RequiresAuthentication()
    {
        HttpClient client = factory.CreateClient().AsUnauthenticated();

        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/dashboard/security/blocklist-url"
        );

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BlocklistUrl_HandsBackAUrlThatTheFeedAccepts()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/dashboard/security/blocklist-url"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("/security/blocklist/");
    }
}
