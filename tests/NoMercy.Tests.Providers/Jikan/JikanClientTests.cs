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
using Moq.Language;
using Moq.Protected;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.Jikan.Models;

namespace NoMercy.Tests.Providers.Jikan;

public class JikanClientTests
{
    private const string SuccessBody = """
        {
          "data": [
            {
              "mal_id": 21,
              "titles": [
                { "type": "Default", "title": "One Piece" },
                { "type": "Japanese", "title": "ワンピース" }
              ],
              "genres": [ { "mal_id": 1, "name": "Action" } ],
              "themes": [ { "mal_id": 40, "name": "Pirates" } ],
              "demographics": [ { "mal_id": 27, "name": "Shounen" } ],
              "year": 1999
            }
          ]
        }
        """;

    [Fact]
    public async Task SearchAsync_WithMatch_ReturnsFirstResult()
    {
        Mock<HttpMessageHandler> handler = new();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SuccessBody),
                }
            );

        HttpClient httpClient = new(handler.Object)
        {
            BaseAddress = new("https://api.jikan.moe/v4/"),
        };

        JikanAnime? result = await JikanClient.SearchAsync(httpClient, "One Piece", 1999);

        result.Should().NotBeNull();
        result!.Demographics.Should().Contain(d => d.Name == "Shounen");
    }

    // /anime/{id} returns a single object under "data" (not an array, unlike
    // /anime search) - GetByIdAsync must unwrap that shape, not the search shape.
    private const string ByIdSuccessBody = """
        {
          "data": {
            "mal_id": 21,
            "titles": [
              { "type": "Default", "title": "One Piece" },
              { "type": "Japanese", "title": "ワンピース" }
            ],
            "genres": [ { "mal_id": 1, "name": "Action" } ],
            "themes": [ { "mal_id": 40, "name": "Pirates" } ],
            "demographics": [ { "mal_id": 27, "name": "Shounen" } ],
            "year": 1999
          }
        }
        """;

    [Fact]
    public async Task GetByIdAsync_WithMatch_ReturnsAnime()
    {
        Mock<HttpMessageHandler> handler = new();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ByIdSuccessBody),
                }
            );

        HttpClient httpClient = new(handler.Object)
        {
            BaseAddress = new("https://api.jikan.moe/v4/"),
        };

        JikanAnime? result = await JikanClient.GetByIdAsync(httpClient, 21);

        result.Should().NotBeNull();
        result!.Demographics.Should().Contain(d => d.Name == "Shounen");
    }

    // Distinguishes GetByIdAsync's own success/failure branch from
    // SearchAsync's - a shared bug in the two "if (!IsSuccessStatusCode)
    // return null" checks would still pass GetByIdAsync_WithMatch_ReturnsAnime
    // alone, since that test only exercises the success path.
    [Fact]
    public async Task GetByIdAsync_NonSuccessStatus_ReturnsNull()
    {
        Mock<HttpMessageHandler> handler = new();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        HttpClient httpClient = new(handler.Object)
        {
            BaseAddress = new("https://api.jikan.moe/v4/"),
        };

        JikanAnime? result = await JikanClient.GetByIdAsync(httpClient, 999999);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetGenresAsync_RequestsTheFilterAndReturnsTheList()
    {
        Uri? requested = null;
        Mock<HttpMessageHandler> handler = new();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Returns<HttpRequestMessage, CancellationToken>(
                (request, _) =>
                {
                    requested = request.RequestUri;
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                """
                                { "data": [ { "mal_id": 27, "name": "Shounen" }, { "mal_id": 25, "name": "Shoujo" } ] }
                                """
                            ),
                        }
                    );
                }
            );

        HttpClient httpClient = new(handler.Object)
        {
            BaseAddress = new("https://api.jikan.moe/v4/"),
        };

        JikanGenre[]? result = await JikanClient.GetGenresAsync(httpClient, "demographics");

        requested!
            .ToString()
            .Should()
            .Be("https://api.jikan.moe/v4/genres/anime?filter=demographics");
        result!.Select(genre => genre.Name).Should().Equal("Shounen", "Shoujo");
    }

    [Fact]
    public async Task GetGenresAsync_NotFound_ReturnsNull()
    {
        HttpClient httpClient = StubClient(new HttpResponseMessage(HttpStatusCode.NotFound));

        JikanGenre[]? result = await JikanClient.GetGenresAsync(httpClient, "themes");

        result.Should().BeNull();
    }

    // A rate limit or a gateway failure is not "no such list": it has to reach
    // the provider queue as an HttpRequestException carrying the status, so the
    // queue retries it with backoff instead of the seed silently storing nothing.
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task GetGenresAsync_RetryableFailure_ThrowsWithTheStatus(HttpStatusCode status)
    {
        HttpClient httpClient = StubClient(new HttpResponseMessage(status));

        Func<Task> act = () => JikanClient.GetGenresAsync(httpClient, "themes");

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should()
            .Be(status);
    }

    [Fact]
    public async Task GetGenresAsync_RateLimitedThenOk_ThrowsOnceThenReturnsTheList()
    {
        HttpClient httpClient = StubClient(
            RateLimited(TimeSpan.FromMilliseconds(50)),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    { "data": [ { "mal_id": 27, "name": "Shounen" } ] }
                    """
                ),
            }
        );

        Func<Task> firstCall = () => JikanClient.GetGenresAsync(httpClient, "demographics");
        await firstCall.Should().ThrowAsync<HttpRequestException>();

        JikanGenre[]? retried = await JikanClient.GetGenresAsync(httpClient, "demographics");

        retried!.Select(genre => genre.Name).Should().Equal("Shounen");
    }

    // SearchAsync paces through the shared Jikan queue, which owns the retry: a
    // rate-limited answer followed by a good one yields the match from the retry,
    // not a null that reads as "no match".
    [Fact]
    public async Task SearchAsync_RateLimitedThenOk_ReturnsTheMatchFromTheRetry()
    {
        HttpClient httpClient = StubClient(
            RateLimited(TimeSpan.FromMilliseconds(50)),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SuccessBody) }
        );

        JikanAnime? result = await JikanClient.SearchAsync(httpClient, "One Piece", 1999);

        result.Should().NotBeNull();
        result!.MalId.Should().Be(21);
    }

    private static HttpResponseMessage RateLimited(TimeSpan retryAfter) =>
        new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(retryAfter) } };

    // Answers the requests in order, one response each.
    private static HttpClient StubClient(params HttpResponseMessage[] responses)
    {
        Mock<HttpMessageHandler> handler = new();
        ISetupSequentialResult<Task<HttpResponseMessage>> sequence = handler
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            );
        foreach (HttpResponseMessage response in responses)
            sequence = sequence.ReturnsAsync(response);

        return new(handler.Object) { BaseAddress = new("https://api.jikan.moe/v4/") };
    }
}
