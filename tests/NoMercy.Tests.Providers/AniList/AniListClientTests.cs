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
}
