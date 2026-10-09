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
using NoMercy.Providers.Helpers;
using NoMercy.Providers.MusicBrainz.Client;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Tests.Common.Providers;

namespace NoMercy.Tests.Providers.MusicBrainz.Client;

/// <summary>
/// Requirement-driven coverage for <see cref="MusicBrainzBaseClient"/>'s
/// request/response contract, exercised through the mock HTTP harness instead
/// of the real network. MusicBrainz previously had zero mocked coverage — every
/// existing "test" for it (see <see cref="NoMercy.Tests.Providers.Helpers.HttpClientProviderTests"/>)
/// only asserted on <see cref="HttpClient.BaseAddress"/>, never on a request or
/// response.
/// </summary>
[Collection("HttpClientProvider")]
public sealed class MusicBrainzBaseClientTests : ProviderHttpHarness
{
    public MusicBrainzBaseClientTests()
        : base(HttpClientNames.MusicBrainz) { }

    private sealed class TestableClient : MusicBrainzBaseClient
    {
        public new Task<T?> Get<T>(
            string url,
            Dictionary<string, string?>? query = null,
            bool? priority = false,
            bool skipCache = false,
            TimeSpan? maxCacheAge = null
        )
            where T : class => base.Get<T>(url, query, priority, skipCache, maxCacheAge);
    }

    [Fact]
    public async Task Get_Success_SendsDefensiveUserAgentAndMapsResponse()
    {
        // Requirement: MusicBrainz rejects anonymous user-agents with a 403, so
        // ConfigureClient must add one whenever the DI-registered HttpClient
        // arrives with none set (the harness registers the client bare, exactly
        // like an "early seed caller" per the production comment).
        string path = Unique("artist");
        MusicBrainzArtist body = new() { Id = Guid.NewGuid(), Name = "Test Artist" };
        Handler.WhenGet(path, MockResponse.Json(HttpStatusCode.OK, body));

        using TestableClient client = new();
        MusicBrainzArtist? result = await client.Get<MusicBrainzArtist>(
            path,
            new() { ["fmt"] = "json" }
        );

        result.Should().NotBeNull();
        result!.Id.Should().Be(body.Id);
        result.Name.Should().Be("Test Artist");

        CapturedRequest request = Handler
            .Requests.Should()
            .ContainSingle(r => r.Path.Contains(path))
            .Which;
        // HttpHeaders re-tokenizes User-Agent's product/comment syntax when it
        // merges HttpClient.DefaultRequestHeaders onto the outgoing request, so
        // assert on the leading product token rather than exact string equality
        // — the requirement under test is "a defensive UA was added at all",
        // not byte-for-byte header serialization.
        request.HasHeader("User-Agent").Should().BeTrue();
        request.HeaderValue("User-Agent").Should().StartWith("NoMercy");
        request.Query.Should().ContainKey("fmt").WhoseValue.Should().Be("json");
    }

    [Fact]
    public async Task Get_NotFound_SoftFailsToNullWithoutThrowing()
    {
        // Requirement: MusicBrainz's 404 for an unknown MBID is "no result", not
        // an error the caller must handle.
        string path = Unique("artist");
        Handler.WhenGet(path, MockResponse.Status(HttpStatusCode.NotFound));

        using TestableClient client = new();
        MusicBrainzArtist? result = await client.Get<MusicBrainzArtist>(path);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_MalformedJsonBody_ReturnsNullInsteadOfThrowing()
    {
        // Requirement: a 200 response with an unparsable body must degrade to
        // null (FromJson<T> is deliberately forgiving) rather than bubble a
        // JsonException up through every provider caller.
        string path = Unique("artist");
        Handler.WhenGet(path, MockResponse.Malformed());

        using TestableClient client = new();
        MusicBrainzArtist? result = await client.Get<MusicBrainzArtist>(path);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_TransientTooManyRequests_RetriesOnceThenReturnsData()
    {
        // Requirement: the shared Queue retries a transient 429/502/503/504
        // with exponential backoff before the caller ever sees a failure — the
        // provider client itself has no retry loop of its own.
        string path = Unique("artist");
        MusicBrainzArtist body = new() { Id = Guid.NewGuid(), Name = "Recovered After Retry" };
        Handler.WhenGet(
            path,
            [
                MockResponse.Status(HttpStatusCode.TooManyRequests),
                MockResponse.Json(HttpStatusCode.OK, body),
            ]
        );

        using TestableClient client = new();
        MusicBrainzArtist? result = await client.Get<MusicBrainzArtist>(path);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Recovered After Retry");
        Handler.RequestCountFor(path).Should().Be(2);
    }

    [Fact]
    public async Task Get_TransientFailureExhaustsRetryBudget_SoftFailsToNull()
    {
        // The shared queue retries a persistent 429 before the provider client
        // returns no result, allowing the importing job to continue.
        string path = Unique("artist");
        Handler.WhenGet(path, MockResponse.Status(HttpStatusCode.TooManyRequests));

        using TestableClient client = new();
        MusicBrainzArtist? result = await client.Get<MusicBrainzArtist>(path);
        result.Should().BeNull();
        Handler.RequestCountFor(path).Should().Be(4); // 1 initial attempt + 3 retries
    }
}
