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
using NoMercy.Providers.TVDB.Client;
using NoMercy.Providers.TVDB.Models.Series;
using NoMercy.Providers.TVDB.Models.Shared;
using NoMercy.Tests.Common.Providers;

namespace NoMercy.Tests.Providers.TVDB.Client;

/// <summary>
/// Requirement-driven request/response coverage for <see cref="TvdbSeriesClient"/>
/// — the concrete client used by the show-import pipeline. TVDB's static
/// login token (see <see cref="TvdbTokenAccess"/>) is pre-seeded per test so
/// these tests exercise URL/query building and response mapping without also
/// re-testing the login flow (covered by <see cref="TvdbBaseClientHarnessTests"/>).
/// </summary>
[Collection("HttpClientProvider")]
public sealed class TvdbSeriesClientTests : ProviderHttpHarness
{
    public TvdbSeriesClientTests()
        : base(HttpClientNames.Tvdb, HttpClientNames.TvdbLogin)
    {
        TvdbTokenAccess.Set(
            new()
            {
                Status = "success",
                Data = new() { Token = "test-token", ExpiresAt = DateTime.UtcNow.AddMonths(1) },
            }
        );
    }

    public override void Dispose()
    {
        TvdbTokenAccess.Reset();
        base.Dispose();
    }

    [Fact]
    public async Task Details_RequestsSeriesByIdAndMapsName()
    {
        const int seriesId = 121361; // Game of Thrones
        TvdbSeriesResponse body = new()
        {
            Status = "success",
            Data = new() { Id = seriesId, Name = "Game of Thrones" },
        };
        Handler.WhenGet($"series/{seriesId}", MockResponse.Json(HttpStatusCode.OK, body));

        using TvdbSeriesClient client = new(seriesId);
        TvdbSeriesResponse? result = await client.Details();

        result.Should().NotBeNull();
        result!.Data.Name.Should().Be("Game of Thrones");

        CapturedRequest request = Handler
            .Requests.Should()
            .ContainSingle(r =>
                r.Path.Contains($"series/{seriesId}") && !r.Path.Contains("extended")
            )
            .Which;
        request.Path.Should().Be($"/v4/series/{seriesId}");
    }

    [Fact]
    public async Task Extended_WithMetaAndShort_BuildsMetaAndShortQueryParameters()
    {
        const int seriesId = 121361;
        TvdbSeriesExtendedResponse body = new()
        {
            Status = "success",
            Data = new() { Id = seriesId, Name = "Game of Thrones" },
        };
        Handler.WhenGet($"series/{seriesId}/extended", MockResponse.Json(HttpStatusCode.OK, body));

        using TvdbSeriesClient client = new(seriesId);
        TvdbSeriesExtendedResponse? result = await client.Extended("translations,episodes", true);

        result.Should().NotBeNull();

        CapturedRequest request = Handler.Requests.Should().ContainSingle().Which;
        request.Path.Should().Be($"/v4/series/{seriesId}/extended");
        request.Query.Should().ContainKey("meta").WhoseValue.Should().Be("translations,episodes");
        request.Query.Should().ContainKey("short").WhoseValue.Should().Be("true");
    }

    [Fact]
    public async Task Extended_WithoutMetaOrShort_OmitsBothQueryParameters()
    {
        const int seriesId = 121361;
        TvdbSeriesExtendedResponse body = new()
        {
            Status = "success",
            Data = new() { Id = seriesId },
        };
        Handler.WhenGet($"series/{seriesId}/extended", MockResponse.Json(HttpStatusCode.OK, body));

        using TvdbSeriesClient client = new(seriesId);
        await client.Extended();

        CapturedRequest request = Handler.Requests.Should().ContainSingle().Which;
        request.Query.Should().NotContainKey("meta");
        request.Query.Should().NotContainKey("short");
    }

    [Fact]
    public async Task Episodes_BuildsSeasonTypeInPathAndPageInQuery()
    {
        const int seriesId = 121361;
        TvdbSeriesEpisodesResponse body = new() { Status = "success" };
        Handler.WhenGet(
            $"series/{seriesId}/episodes/official",
            MockResponse.Json(HttpStatusCode.OK, body)
        );

        using TvdbSeriesClient client = new(seriesId);
        TvdbSeriesEpisodesResponse? result = await client.Episodes("official", page: 2);

        result.Should().NotBeNull();

        CapturedRequest request = Handler.Requests.Should().ContainSingle().Which;
        request.Path.Should().Be($"/v4/series/{seriesId}/episodes/official");
        request.Query.Should().ContainKey("page").WhoseValue.Should().Be("2");
    }

    [Fact]
    public async Task Filter_MapsEveryFilterFieldToItsOwnQueryParameter()
    {
        TvdbSeriesFilter filter = new()
        {
            Country = "usa",
            Language = "eng",
            CompanyId = 42,
            ContentRatingId = 7,
            GenreIds = "18,10765",
            SortBy = 1,
            SortType = "asc",
            Status = 1,
            Year = 2011,
            Page = 3,
        };
        TvdbPaginatedResponse<TvdbSeries> body = new() { Status = "success" };
        Handler.WhenGet("series/filter", MockResponse.Json(HttpStatusCode.OK, body));

        using TvdbSeriesClient client = new();
        await client.Filter(filter);

        CapturedRequest request = Handler.Requests.Should().ContainSingle().Which;
        request.Query["country"].Should().Be("usa");
        request.Query["lang"].Should().Be("eng");
        request.Query["company"].Should().Be("42");
        request.Query["contentRating"].Should().Be("7");
        request.Query["genre"].Should().Be("18,10765");
        request.Query["sort"].Should().Be("1");
        request.Query["sortType"].Should().Be("asc");
        request.Query["status"].Should().Be("1");
        request.Query["year"].Should().Be("2011");
        request.Query["page"].Should().Be("3");
    }

    [Fact]
    public async Task Details_UnknownSeriesId_ReturnsNull()
    {
        const int unknownId = 999_999_999;
        Handler.WhenGet($"series/{unknownId}", MockResponse.Status(HttpStatusCode.NotFound));

        using TvdbSeriesClient client = new(unknownId);
        TvdbSeriesResponse? result = await client.Details();

        result.Should().BeNull();
    }
}
