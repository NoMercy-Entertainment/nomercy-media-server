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
using NoMercy.NmSystem.Configuration;
using NoMercy.Providers.Helpers;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Tests.Common.Providers;
using NoMercy.Tests.Providers.TMDB.Mocks;

namespace NoMercy.Tests.Providers.TMDB.Client;

/// <summary>
/// Requirement-driven coverage for <see cref="TmdbMovieClient"/> through the
/// mock HTTP harness — no real network call, no dependency on a live TMDB
/// token. This complements (does not replace) the existing <c>[Collection("TmdbApi")]</c>
/// suite, which exercises the client against the real TMDB API for behavioral
/// smoke coverage but never asserts on the outgoing request itself.
/// </summary>
[Collection("HttpClientProvider")]
public sealed class TmdbMovieClientHarnessTests : ProviderHttpHarness
{
    public TmdbMovieClientHarnessTests()
        : base(HttpClientNames.Tmdb) { }

    [Fact]
    public async Task Details_WithPriority_SendsAuthHeaderAndCallerLanguage()
    {
        // Requirement: Details(priority: true) must carry the Bearer token
        // from ApiKeyStore and the client's constructed language, joined with
        // ",null" (TmdbBaseClient's fallback-language convention).
        // Every test in this file uses its own movie id so the shared on-disk
        // CacheController cache (keyed by URL, incl. query) never answers one
        // test's request with another test's cached body.
        TestApiKeyStore.Instance.TmdbToken = "tmdb-test-token";
        const int movieId = 900_001;
        TmdbMovieDetails body = TmdbMovieMockData.GetSampleMovieDetails();
        Handler.WhenGet($"movie/{movieId}", MockResponse.Json(HttpStatusCode.OK, body));

        using TmdbMovieClient client = new(movieId, language: "nl-NL");
        TmdbMovieDetails? result = await client.Details(priority: true);

        result.Should().NotBeNull();
        result!.Title.Should().Be("The Dark Knight");

        CapturedRequest request = Handler.Requests.Should().ContainSingle().Which;
        request.Path.Should().Be($"/3/movie/{movieId}");
        request.HeaderValue("Authorization").Should().Be("Bearer tmdb-test-token");
        request.Query.Should().ContainKey("language").WhoseValue.Should().Be("nl-NL,null");
    }

    [Fact]
    public async Task Details_WithoutPriority_SendsEmptyLanguageQuery()
    {
        // Requirement: non-priority calls deliberately send language="" so
        // TMDB falls back to its own default rather than forcing a
        // background/bulk-fill request to wait on translation availability.
        TestApiKeyStore.Instance.TmdbToken = "tmdb-test-token";
        const int movieId = 900_002;
        TmdbMovieDetails body = TmdbMovieMockData.GenerateMovieWithId(movieId);
        Handler.WhenGet($"movie/{movieId}", MockResponse.Json(HttpStatusCode.OK, body));

        using TmdbMovieClient client = new(movieId, language: "nl-NL");
        TmdbMovieDetails? result = await client.Details();

        result.Should().NotBeNull();

        CapturedRequest request = Handler.Requests.Should().ContainSingle().Which;
        request.Query.Should().ContainKey("language").WhoseValue.Should().Be(string.Empty);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task Details_ReflectsAdultContentSetting_InIncludeAdultQuery(
        bool allowAdultContent,
        string expectedQueryValue
    )
    {
        TestApiKeyStore.Instance.TmdbToken = "tmdb-test-token";
        RuntimeServerSettings.Current.AllowAdultContent = allowAdultContent;
        try
        {
            int movieId = allowAdultContent ? 900_003 : 900_004;
            TmdbMovieDetails body = TmdbMovieMockData.GenerateMovieWithId(movieId);
            Handler.WhenGet($"movie/{movieId}", MockResponse.Json(HttpStatusCode.OK, body));

            using TmdbMovieClient client = new(movieId);
            await client.Details();

            CapturedRequest request = Handler.Requests.Should().ContainSingle().Which;
            request
                .Query.Should()
                .ContainKey("include_adult")
                .WhoseValue.Should()
                .Be(expectedQueryValue);
        }
        finally
        {
            RuntimeServerSettings.Current.AllowAdultContent = null;
        }
    }

    [Fact]
    public async Task WithAllAppends_NoMockProviderConfigured_BuildsFixedAppendToResponseList()
    {
        // Requirement: WithAllAppends() (real path, no mockAppendsProvider
        // delegate) must request exactly the fixed append set the movie
        // import pipeline expects, comma-joined via append_to_response.
        TestApiKeyStore.Instance.TmdbToken = "tmdb-test-token";
        const int movieId = 900_005;
        TmdbMovieAppends body = TmdbMovieMockData.GetSampleMovieAppends();
        Handler.WhenGet($"movie/{movieId}", MockResponse.Json(HttpStatusCode.OK, body));

        using TmdbMovieClient client = new(movieId);
        TmdbMovieAppends? result = await client.WithAllAppends();

        result.Should().NotBeNull();
        result!.Credits.Should().NotBeNull();

        CapturedRequest request = Handler.Requests.Should().ContainSingle().Which;
        request
            .Query.Should()
            .ContainKey("append_to_response")
            .WhoseValue.Should()
            .Be(
                "alternative_titles,release_dates,changes,credits,keywords,recommendations,"
                    + "similar,translations,external_ids,videos,images,watch/providers"
            );
    }

    [Fact]
    public async Task Details_UnknownMovieId_ReturnsNullWithoutThrowing()
    {
        TestApiKeyStore.Instance.TmdbToken = "tmdb-test-token";
        const int unknownId = 999_999_999;
        Handler.WhenGet($"movie/{unknownId}", MockResponse.Status(HttpStatusCode.NotFound));

        using TmdbMovieClient client = new(unknownId);
        TmdbMovieDetails? result = await client.Details();

        result.Should().BeNull();
    }

    [Fact]
    public async Task Details_TransientServiceUnavailable_RetriesOnceThenReturnsData()
    {
        // Requirement: the shared Queue retries a transient 503 before the
        // caller ever sees a failure.
        TestApiKeyStore.Instance.TmdbToken = "tmdb-test-token";
        const int movieId = 900_006;
        TmdbMovieDetails body = TmdbMovieMockData.GenerateMovieWithId(movieId);
        Handler.WhenGet(
            $"movie/{movieId}",
            MockResponse.Status(HttpStatusCode.ServiceUnavailable),
            MockResponse.Json(HttpStatusCode.OK, body)
        );

        using TmdbMovieClient client = new(movieId);
        TmdbMovieDetails? result = await client.Details();

        result.Should().NotBeNull();
        Handler.RequestCountFor($"movie/{movieId}").Should().Be(2);
    }

    [Fact]
    public async Task Details_TransientFailureExhaustsRetryBudget_SoftFailsToNullRatherThanThrowing()
    {
        // Requirement: unlike MusicBrainzBaseClient (which only soft-fails
        // 404), TmdbBaseClient.Get<T> explicitly catches ServiceUnavailable
        // and TooManyRequests too — so once the shared Queue's retry budget is
        // exhausted, TMDB degrades to null instead of throwing.
        TestApiKeyStore.Instance.TmdbToken = "tmdb-test-token";
        const int movieId = 900_007;
        Handler.WhenGet($"movie/{movieId}", MockResponse.Status(HttpStatusCode.ServiceUnavailable));

        using TmdbMovieClient client = new(movieId);
        TmdbMovieDetails? result = await client.Details();

        result.Should().BeNull();
        Handler.RequestCountFor($"movie/{movieId}").Should().Be(4); // 1 initial + 3 retries
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("now_playing")]
    [InlineData("top_rated")]
    [InlineData("upcoming")]
    public async Task ListRoutes_ClientHasMovieId_PathOmitsIt(string route)
    {
        // Requirement: latest, now_playing, top_rated and upcoming are TMDB list
        // endpoints (movie/<route>), the same shape TmdbTvClient uses; the
        // client's movie id has no place in them.
        TestApiKeyStore.Instance.TmdbToken = "tmdb-test-token";
        const int movieId = 900_008;
        TmdbMovieDetails movie = TmdbMovieMockData.GenerateMovieWithId(movieId);
        object body =
            route == "latest"
                ? movie
                : new TmdbPaginatedResponse<TmdbMovie>
                {
                    Page = 1,
                    Results = [movie],
                    TotalPages = 1,
                    TotalResults = 1,
                };
        Handler.WhenGet($"movie/{route}", MockResponse.Json(HttpStatusCode.OK, body));

        // List routes carry no id, so a unique language is what keeps the shared
        // on-disk cache from answering this request with another test's body.
        using TmdbMovieClient client = new(movieId, language: $"xx-{Guid.NewGuid():N}");
        object? result = await CallListRoute(client, route);

        CapturedRequest request = Handler.Requests.Should().ContainSingle().Which;
        request.Path.Should().Be($"/3/movie/{route}");
        result.Should().NotBeNull();
    }

    private static async Task<object?> CallListRoute(TmdbMovieClient client, string route) =>
        route switch
        {
            "latest" => await client.Latest(priority: true),
            "now_playing" => await client.NowPlaying(priority: true),
            "top_rated" => await client.TopRated(priority: true),
            "upcoming" => await client.Upcoming(priority: true),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };
}
