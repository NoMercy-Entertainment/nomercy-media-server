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
/// The library section states its own entries.
///
/// <para>
/// Each client used to hold this list: a library loop, seven hard-coded pages
/// and a plugin loop, written once per surface. A viewer with only music was
/// still offered people and specials, and a plugin mounted into this section had
/// nowhere to appear, so the section had no plugin UI at all.
/// </para>
/// </summary>
[Trait("Category", "MediaLibraries")]
public class LibraryNavigationTests : IClassFixture<NoMercyApiFactory>
{
    private const string Route = "/api/v1/setup/navigation";

    private readonly HttpClient _authed;
    private readonly HttpClient _unauthed;

    public LibraryNavigationTests(NoMercyApiFactory factory)
    {
        _authed = factory.CreateClient().AsAuthenticated();
        _unauthed = factory.CreateClient().AsUnauthenticated();
    }

    [Fact]
    public async Task Navigation_IsClosedToAnonymousCallers()
    {
        HttpResponseMessage response = await _unauthed.GetAsync(Route);

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Navigation_AnswersWithTheStandardEnvelope()
    {
        HttpResponseMessage response = await _authed.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()
        );

        document.RootElement.TryGetProperty("data", out JsonElement data).Should().BeTrue();
        data.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // An entry a client cannot draw or navigate to is worse than an absent one:
    // it renders as a blank row that goes nowhere.
    [Fact]
    public async Task EveryEntryCanBeDrawnAndFollowed()
    {
        HttpResponseMessage response = await _authed.GetAsync(Route);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()
        );

        foreach (JsonElement entry in document.RootElement.GetProperty("data").EnumerateArray())
        {
            entry.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("label").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("icon").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("link").GetString().Should().StartWith("/");
            entry.GetProperty("origin").GetString().Should().BeOneOf("library", "page", "plugin");
        }
    }

    // The pages the app ships only exist for someone who has that kind of
    // library: a music-only viewer browsing "people" lands on an empty screen.
    [Fact]
    public async Task ThePagesOfferedFollowTheLibrariesThatExist()
    {
        HttpResponseMessage response = await _authed.GetAsync(Route);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()
        );

        List<JsonElement> entries = [.. document.RootElement.GetProperty("data").EnumerateArray()];

        bool hasVideoLibrary = entries.Any(entry =>
            entry.GetProperty("origin").GetString() == "library"
        );
        bool offersGenres = entries.Any(entry => entry.GetProperty("id").GetString() == "genres");

        offersGenres.Should().Be(hasVideoLibrary);
    }

    // No client has a people list page (web and the app only have /person/{id}); the app crashed on it.
    [Fact]
    public async Task NoEntryLinksToThePeopleListNoClientHas()
    {
        HttpResponseMessage response = await _authed.GetAsync(Route);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()
        );

        document
            .RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("link").GetString())
            .Should()
            .NotContain("/person");
    }
}
