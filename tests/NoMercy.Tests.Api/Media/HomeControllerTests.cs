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
using System.Text.Json;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Home")]
public class HomeControllerTests : IClassFixture<NoMercyApiFactory>
{
    private readonly HttpClient _authed;
    private readonly HttpClient _unauthed;
    private readonly HttpClient _factoryClient;

    public HomeControllerTests(NoMercyApiFactory factory)
    {
        _factoryClient = factory.CreateClient();
        _authed = factory.CreateClient().AsAuthenticated();
        _unauthed = factory.CreateClient().AsUnauthenticated();
    }

    [Fact]
    public async Task Index_Authenticated_ReturnsPaginatedEnvelope()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1?take=10&page=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);

        Assert.True(
            document.RootElement.TryGetProperty("data", out JsonElement data),
            "Home page response must expose a 'data' array"
        );
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
    }

    [Fact]
    public async Task Index_Unauthenticated_DoesNotReturnOk()
    {
        HttpResponseMessage response = await _unauthed.GetAsync("/api/v1?take=10&page=0");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Index_NonLolomo_Page0_ContainsLatestInLibraryRow()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1?take=10&page=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement data = document.RootElement.GetProperty("data");

        bool hasLatestInRow = data.EnumerateArray()
            .Any(row =>
                row.TryGetProperty("title", out JsonElement title)
                && title.GetString() is not null
                && title.GetString()!.StartsWith("Latest in ", StringComparison.Ordinal)
            );

        Assert.True(
            hasLatestInRow,
            "Non-lolomo home page 0 must include a 'Latest in {library}' row"
        );
    }

    [Fact]
    public async Task Index_Lolomo_Page0_DoesNotContainLatestInLibraryRow()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            "/api/v1?take=10&page=0&version=lolomo"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement data = document.RootElement.GetProperty("data");

        bool hasLatestInRow = data.EnumerateArray()
            .Any(row =>
                row.TryGetProperty("title", out JsonElement title)
                && title.GetString() is not null
                && title.GetString()!.StartsWith("Latest in ", StringComparison.Ordinal)
            );

        Assert.False(
            hasLatestInRow,
            "Lolomo home page 0 must not include a 'Latest in {library}' row"
        );
    }

    // The gate above covers the Index endpoint (/api/v1). The apps call /api/v1/home, which
    // is a different action returning components rather than genre rows, and it honoured no
    // version at all — so the rows the lolomo clients build themselves arrived duplicated.
    [Fact]
    public async Task Home_Lolomo_OmitsLatestInLibraryRow()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/home?version=lolomo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement data = document.RootElement.GetProperty("data");

        bool hasLatestInRow = data.EnumerateArray()
            .Any(component =>
                component.TryGetProperty("props", out JsonElement props)
                && props.TryGetProperty("title", out JsonElement title)
                && title.GetString() is not null
                && title.GetString()!.StartsWith("Latest in ", StringComparison.Ordinal)
            );

        Assert.False(hasLatestInRow, "Lolomo /home must not include a 'Latest in {library}' row");
    }

    // The secondary seeded user is a plain member with no libraries, so their Home is empty.
    // The empty state used to offer "Add library", which the apps route to a dashboard page
    // a member cannot open, so the button bounced them straight back to Home.
    [Fact]
    public async Task Home_EmptyForMember_DoesNotOfferAddLibrary()
    {
        HttpClient member = _factoryClient.AsSecondaryUser();

        HttpResponseMessage response = await member.GetAsync("/api/v1/home");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("No libraries yet", body);
        Assert.DoesNotContain("Add library", body);
        Assert.DoesNotContain("/dashboard/libraries", body);
    }
}
