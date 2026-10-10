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

using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Movies;
using NoMercy.Providers.TMDB.Models.Collections;
using NoMercy.Providers.TMDB.Models.Movies;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Collections")]
public class CollectionResponseItemDtoTests
{
    [Fact]
    public void ItemPosters_EmptyCollectionsSerializeAsArrays()
    {
        CollectionsResponseItemDto list = new(new Collection { Id = 1, Title = "Empty" });
        CollectionResponseItemDto detail = new(
            new TmdbCollectionAppends
            {
                Id = 1,
                Name = "Empty",
                Parts = [],
            }
        );

        Assert.Empty(list.ItemPosters);
        Assert.Empty(detail.ItemPosters);
        Assert.Contains("\"item_posters\":[]", Newtonsoft.Json.JsonConvert.SerializeObject(list));
        Assert.Contains("\"item_posters\":[]", Newtonsoft.Json.JsonConvert.SerializeObject(detail));
    }

    [Fact]
    public void ItemPosters_SkipMissingPostersAndSortByDateThenId()
    {
        Collection collection = new() { Id = 2, Title = "Some" };
        collection.CollectionMovies =
        [
            new()
            {
                MovieId = 4,
                Movie = new()
                {
                    Id = 4,
                    ReleaseDate = new(2020, 1, 1),
                    Poster = "/four",
                },
            },
            new()
            {
                MovieId = 3,
                Movie = new()
                {
                    Id = 3,
                    ReleaseDate = new(2019, 1, 1),
                    Poster = null,
                },
            },
            new()
            {
                MovieId = 2,
                Movie = new()
                {
                    Id = 2,
                    ReleaseDate = new(2020, 1, 1),
                    Poster = "/two",
                },
            },
            new()
            {
                MovieId = 1,
                Movie = new() { Id = 1, Poster = "/undated" },
            },
        ];

        CollectionsResponseItemDto list = new(collection);

        Assert.Equal(["/two", "/four", "/undated"], list.ItemPosters);
    }

    [Fact]
    public void ItemPosters_LimitToNineInReleaseOrder()
    {
        TmdbCollectionAppends appends = new()
        {
            Id = 3,
            Name = "Many",
            Parts =
            [
                .. Enumerable
                    .Range(1, 12)
                    .Reverse()
                    .Select(id => new TmdbMovie
                    {
                        Id = id,
                        Title = $"Movie {id}",
                        ReleaseDate = new(2000 + id, 1, 1),
                        PosterPath = $"/{id}",
                    }),
            ],
        };

        CollectionResponseItemDto detail = new(appends);

        Assert.Equal(Enumerable.Range(1, 9).Select(id => $"/{id}"), detail.ItemPosters);
    }

    [Fact]
    public void ItemPosters_ReachCurrentCollectionCardResponse()
    {
        CollectionListDto list = new()
        {
            Id = 4,
            Title = "Card",
            ItemPosters = ["/one", "/two"],
        };

        CardData card = new(list);

        Assert.Equal(list.ItemPosters, card.ItemPosters);
        Assert.Contains(
            "\"item_posters\":[\"/one\",\"/two\"]",
            Newtonsoft.Json.JsonConvert.SerializeObject(card)
        );
    }

    [Fact]
    public void ItemPosters_ReachLocalCollectionDetailResponse()
    {
        Collection collection = new() { Id = 5, Title = "Detail" };
        collection.CollectionMovies =
        [
            new()
            {
                MovieId = 2,
                Movie = new()
                {
                    Id = 2,
                    ReleaseDate = new(2022, 1, 1),
                    Poster = "/later",
                },
            },
            new()
            {
                MovieId = 1,
                Movie = new()
                {
                    Id = 1,
                    ReleaseDate = new(2020, 1, 1),
                    Poster = "/earlier",
                },
            },
        ];

        CollectionResponseItemDto detail = new(collection);

        Assert.Equal(["/earlier", "/later"], detail.ItemPosters);
    }

    [Fact]
    public void Ctor_FromTmdbCollectionAppends_KeepsTranslatedTitleAndOverview()
    {
        TmdbCollectionAppends appends = new()
        {
            Id = 10,
            Name = "The Matrix Collection",
            Overview = "English overview.",
            Parts =
            [
                new()
                {
                    Id = 1,
                    Title = "The Matrix",
                    VoteAverage = 8.7,
                },
            ],
            Translations = new()
            {
                Translations =
                [
                    new()
                    {
                        Iso6391 = "nl",
                        Data = new()
                        {
                            Title = "De Matrix Collectie",
                            Overview = "Nederlandse samenvatting.",
                        },
                    },
                ],
            },
        };

        CollectionResponseItemDto dto = new(appends);

        Assert.Equal("De Matrix Collectie", dto.Title);
        Assert.Equal("Nederlandse samenvatting.", dto.Overview);
    }

    [Fact]
    public void Ctor_FromTmdbCollectionAppends_FallsBackToEnglish_WhenNoTranslation()
    {
        TmdbCollectionAppends appends = new()
        {
            Id = 11,
            Name = "The Matrix Collection",
            Overview = "English overview.",
            Parts =
            [
                new()
                {
                    Id = 1,
                    Title = "The Matrix",
                    VoteAverage = 8.7,
                },
            ],
            Translations = new() { Translations = [] },
        };

        CollectionResponseItemDto dto = new(appends);

        Assert.Equal("The Matrix Collection", dto.Title);
        Assert.Equal("English overview.", dto.Overview);
    }
}
