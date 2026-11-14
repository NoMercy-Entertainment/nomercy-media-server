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
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.Providers.Helpers;
using NoMercy.Setup.Server;
using NoMercy.Tests.Common.Providers;

namespace NoMercy.Tests.MediaProcessing.Inbox;

[Collection("HttpClientProvider")]
public sealed class TvdbIdentityFallbackTests : ProviderHttpHarness
{
    private readonly string _previousTvdbKey;

    public TvdbIdentityFallbackTests()
        : base(HttpClientNames.Tmdb, HttpClientNames.Tvdb, HttpClientNames.TvdbLogin)
    {
        ApiKeyStore keys = (ApiKeyStore)ApiKeyStore.Current;
        _previousTvdbKey = keys.TvdbKey;
        keys.TvdbKey = "test-tvdb-key";
        Handler.WhenPost(
            "/login",
            MockResponse.Json(
                HttpStatusCode.OK,
                """{"status":"success","data":{"token":"test-token"}}"""
            )
        );
    }

    public override void Dispose()
    {
        ((ApiKeyStore)ApiKeyStore.Current).TvdbKey = _previousTvdbKey;
        typeof(NoMercy.Providers.TVDB.Client.TvdbBaseClient)
            .GetProperty(
                "Token",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
            )!
            .SetValue(null, null);
        base.Dispose();
        HttpClientProvider.Initialize(new TmdbMockHttpClientFactory());
    }

    [Theory]
    [InlineData(false, "movie", "movie")]
    [InlineData(true, "tv", "series")]
    public async Task EmptyTmdbSearch_UsesTvdbCrossReferenceAsTmdbIdentity(
        bool isTv,
        string tmdbType,
        string tvdbType
    )
    {
        string title = "Fallback Probe " + Guid.NewGuid().ToString("N");
        Handler.WhenGet(
            "/search/" + tmdbType,
            MockResponse.Json(HttpStatusCode.OK, """{"page":1,"results":[]}""")
        );
        Handler.WhenGet(
            "/v4/search",
            MockResponse.Json(
                HttpStatusCode.OK,
                """
                {"status":"success","data":[{"id":"9001","name":"Fallback Probe","year":"2020",
                  "remote_ids":[{"id":"550","sourceName":"TheMovieDB.com","type":12}]}]}
                """
            )
        );

        TmdbMusicBrainzMetadataProbe probe = new();
        CandidateMatch[] matches = isTv
            ? await probe.SearchTvAsync(title, 2020, CancellationToken.None)
            : await probe.SearchMoviesAsync(title, 2020, CancellationToken.None);

        CandidateMatch match = matches.Should().ContainSingle().Which;
        match.Provider.Should().Be("tmdb");
        match.ExternalId.Should().Be("550");
        match.Title.Should().Be("Fallback Probe");
        match.Year.Should().Be(2020);
        CapturedRequest request = Handler
            .Requests.Should()
            .ContainSingle(r => r.Path == "/v4/search")
            .Which;
        request.Query["type"].Should().Be(tvdbType);
        request.Query["year"].Should().Be("2020");
    }

    [Fact]
    public async Task TmdbMatch_KeepsExistingResultWithoutCallingTvdb()
    {
        string title = "Primary Probe " + Guid.NewGuid().ToString("N");
        Handler.WhenGet(
            "/search/movie",
            MockResponse.Json(
                HttpStatusCode.OK,
                """{"page":1,"results":[{"id":42,"title":"Primary Probe","popularity":50,"vote_average":8}]}"""
            )
        );

        TmdbMusicBrainzMetadataProbe probe = new();
        CandidateMatch[] matches = await probe.SearchMoviesAsync(
            title,
            null,
            CancellationToken.None
        );

        matches.Should().ContainSingle().Which.ExternalId.Should().Be("42");
        Handler.Requests.Should().NotContain(r => r.Path == "/v4/search");
    }

    [Fact]
    public async Task TvdbResultWithoutTmdbCrossReference_IsNotPassedToTmdbImport()
    {
        string title = "Unlinked Probe " + Guid.NewGuid().ToString("N");
        Handler.WhenGet(
            "/search/movie",
            MockResponse.Json(HttpStatusCode.OK, """{"page":1,"results":[]}""")
        );
        Handler.WhenGet(
            "/v4/search",
            MockResponse.Json(
                HttpStatusCode.OK,
                """{"status":"success","data":[{"id":"9001","name":"Unlinked Probe","remote_ids":[]}]}"""
            )
        );

        TmdbMusicBrainzMetadataProbe probe = new();
        CandidateMatch[] matches = await probe.SearchMoviesAsync(
            title,
            null,
            CancellationToken.None
        );

        matches.Should().BeEmpty();
        Handler.Requests.Should().ContainSingle(r => r.Path == "/v4/search");
    }
}
