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
using NoMercy.Providers.AniList;
using NoMercy.Providers.AniList.Models;

namespace NoMercy.Tests.Providers.AniList;

public class AniListClientTests
{
    private const string SuccessBody = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "id": 21,
                  "idMal": 99999,
                  "title": { "romaji": "One Piece", "english": "One Piece", "native": "ワンピース" },
                  "synonyms": ["OP"],
                  "countryOfOrigin": "JP",
                  "seasonYear": 1999,
                  "season": "FALL",
                  "genres": ["Action", "Adventure"],
                  "tags": [ { "name": "Pirates", "category": "Setting-Universe", "isAdult": false } ]
                }
              ]
            }
          }
        }
        """;

    [Fact]
    public async Task SearchAsync_WithMatch_ReturnsMappedMedia()
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
            BaseAddress = new("https://graphql.anilist.co/"),
        };

        AniListMedia? result = await AniListClient.SearchAsync(httpClient, "One Piece", 1999);

        result.Should().NotBeNull();
        result!.Id.Should().Be(21);
        // Distinct from Id on purpose - a mapping bug that reads "id" for both
        // AniList's own id and the MAL cross-reference would still pass every
        // other assertion here.
        result.IdMal.Should().Be(99999);
        result.CountryOfOrigin.Should().Be("JP");
        result.Genres.Should().Contain("Action");
        result.Tags.Should().Contain(tag => tag.Name == "Pirates");
    }

    [Fact]
    public async Task SearchAsync_RequestsTagDescription_AndMapsIt()
    {
        string? sentBody = null;
        Mock<HttpMessageHandler> handler = new();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Returns<HttpRequestMessage, CancellationToken>(
                async (request, _) =>
                {
                    sentBody = await request.Content!.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            { "data": { "Page": { "media": [ { "id": 1, "title": {}, "tags": [
                              { "name": "Isekai", "description": "Characters are transported to another world.", "category": "Theme-Fantasy", "isAdult": false }
                            ] } ] } } }
                            """
                        ),
                    };
                }
            );

        HttpClient httpClient = new(handler.Object)
        {
            BaseAddress = new("https://graphql.anilist.co/"),
        };

        AniListMedia? result = await AniListClient.SearchAsync(httpClient, "Isekai", null);

        sentBody.Should().Contain("tags { name description category isAdult }");
        result!
            .Tags.Single()
            .Description.Should()
            .Be("Characters are transported to another world.");
    }

    [Fact]
    public async Task GetTagCollectionAsync_ReturnsEveryTagWithDescription()
    {
        string? sentBody = null;
        Mock<HttpMessageHandler> handler = new();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Returns<HttpRequestMessage, CancellationToken>(
                async (request, _) =>
                {
                    sentBody = await request.Content!.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            { "data": { "MediaTagCollection": [
                              { "name": "Isekai", "description": "Another world.", "category": "Theme-Fantasy", "isAdult": false },
                              { "name": "Ecchi", "description": null, "category": "Theme-Other", "isAdult": true }
                            ] } }
                            """
                        ),
                    };
                }
            );

        HttpClient httpClient = new(handler.Object)
        {
            BaseAddress = new("https://graphql.anilist.co/"),
        };

        AniListMediaTag[]? result = await AniListClient.GetTagCollectionAsync(httpClient);

        sentBody.Should().Contain("MediaTagCollection");
        result.Should().NotBeNull();
        result!.Select(tag => tag.Name).Should().Equal("Isekai", "Ecchi");
        result[0].Description.Should().Be("Another world.");
        result[1].IsAdult.Should().BeTrue();
    }

    [Fact]
    public async Task GetTagCollectionAsync_WhenServerFails_ReturnsNull()
    {
        Mock<HttpMessageHandler> handler = new();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        HttpClient httpClient = new(handler.Object)
        {
            BaseAddress = new("https://graphql.anilist.co/"),
        };

        AniListMediaTag[]? result = await AniListClient.GetTagCollectionAsync(httpClient);

        result.Should().BeNull();
    }
}
