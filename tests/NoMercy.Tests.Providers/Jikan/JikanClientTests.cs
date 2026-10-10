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
}
