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

/// <summary>
/// Covers GET /api/v1/userData/favorites. NoMercyApiFactory seeds exactly one
/// favorited movie, tv show, collection and special for the default test
/// user — see NoMercyApiFactory.SeedMediaData "Step 8". Coverage for
/// per-user isolation and the empty-favorites case lives in
/// HomeRepositoryFavoritesTests, since the shared test-auth handler here
/// only impersonates a single fixed identity.
/// </summary>
[Trait("Category", "UserData")]
public class UserDataFavoritesControllerTests : IClassFixture<NoMercyApiFactory>
{
    private readonly HttpClient _authed;
    private readonly HttpClient _unauthed;

    public UserDataFavoritesControllerTests(NoMercyApiFactory factory)
    {
        _authed = factory.CreateClient().AsAuthenticated();
        _unauthed = factory.CreateClient().AsUnauthenticated();
    }

    [Fact]
    public async Task Favorites_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync("/api/v1/userData/favorites");

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Favorites_ReturnsOk_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/userData/favorites");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Favorites_ReturnsEnvelopeWithDataProperty()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/userData/favorites");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        doc.RootElement.TryGetProperty("data", out JsonElement data)
            .Should()
            .BeTrue("favorites response envelope must contain a 'data' property");
        data.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Favorites_ReturnsSeededMovieTvCollectionAndSpecial_OrderedByTitle()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/userData/favorites");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        // The card's displayed name drops a trailing "Collection" word (see
        // CollectionTitleFormatter) -- the seeded collection's stored title is
        // "Test Collection", displayed as "Test".
        string[] seeded = ["Breaking Bad", "Spirited Away", "Test", "Test Special"];

        List<string?> titles = data.EnumerateArray()
            .Select(card => card.GetProperty("title").GetString())
            .ToList();

        // Other test classes sharing this fixture's database legitimately create
        // and self-favorite their own specials (e.g. the dashboard "create empty
        // special" flow), so assert the seeded four are present and in title
        // order relative to each other rather than an exact, closed set.
        titles.Should().Contain(seeded);
        titles.Where(title => seeded.Contains(title)).Should().Equal(seeded);
    }

    [Fact]
    public async Task Favorites_ReturnsExpectedTypePerCard()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/userData/favorites");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        Dictionary<string, string?> typeByTitle = data.EnumerateArray()
            .ToDictionary(
                card => card.GetProperty("title").GetString()!,
                card => card.GetProperty("type").GetString()
            );

        typeByTitle["Spirited Away"].Should().Be("movie");
        typeByTitle["Breaking Bad"].Should().Be("tv");
        // Displayed as "Test" -- the card drops the trailing "Collection" word
        // (see CollectionTitleFormatter); the seeded collection's stored title
        // is "Test Collection".
        typeByTitle["Test"].Should().Be("collection");
        typeByTitle["Test Special"].Should().Be("specials");
    }
}
