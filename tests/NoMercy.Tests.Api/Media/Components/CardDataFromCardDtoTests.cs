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

using FluentAssertions;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using Xunit;

namespace NoMercy.Tests.Api.Media.Components;

[Trait("Category", "Unit")]
public class CardDataFromCardDtoTests
{
    private static readonly MovieCardDto Movie = new()
    {
        Id = 129,
        Title = "Spirited Away",
        TitleSort = "spirited away",
        Overview = "A girl wanders into the spirit world.",
        Poster = "/poster.jpg",
        Backdrop = "/backdrop.jpg",
        ReleaseDate = new DateTime(2001, 7, 20),
        CreatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
        VideoFileCount = 1,
        CertificationRating = "PG",
        CertificationCountry = "US",
    };

    [Fact]
    public void MovieCard_CarriesEveryFieldOfTheComponentCard()
    {
        NmCardDto expected = new(Movie);

        CardData card = new(Movie);

        ((int)card.Id!).Should().Be(129);
        card.Title.Should().Be(expected.Title);
        card.TitleSort.Should().Be(expected.TitleSort);
        card.Overview.Should().Be(expected.Overview);
        card.Link.OriginalString.Should().Be("/movie/129");
        card.Rating.Should().BeEquivalentTo(expected.Rating);
        card.Year.Should().Be(2001);
        card.Type.Should().Be("movie");
        card.CreatedAt.Should().Be(Movie.CreatedAt);
        card.Poster.Should().Be("/poster.jpg");
        card.Backdrop.Should().Be("/backdrop.jpg");
        card.HaveItems.Should().Be(1);
        card.NumberOfItems.Should().Be(1);
    }

    [Fact]
    public void WatchCard_LinksToThePlayer()
    {
        new CardData(Movie, watch: true).Link.OriginalString.Should().Be("/movie/129/watch");
    }

    [Fact]
    public void TvCard_UsesTheTvType()
    {
        CardData card = new(
            new TvCardDto
            {
                Id = 1399,
                Title = "Game of Thrones",
                TitleSort = "game of thrones",
            }
        );

        card.Type.Should().Be("tv");
        card.Link.OriginalString.Should().Be("/tv/1399");
    }
}
