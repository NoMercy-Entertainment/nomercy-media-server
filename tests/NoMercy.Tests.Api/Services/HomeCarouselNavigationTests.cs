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
using NoMercy.Api.Services;
using Xunit;

namespace NoMercy.Tests.Api.Services;

[Trait("Category", "Unit")]
public class HomeCarouselNavigationTests
{
    private static readonly string[] Carousels = ["library_1", "genre_28", "genre_35"];

    [Fact]
    public void ForCarousel_WithContinueWatching_RingsThroughContinue()
    {
        HomeCarouselNavigation
            .ForCarousel(Carousels, 0, "continue")
            .Should()
            .Be(("continue", "genre_28"));
        HomeCarouselNavigation
            .ForCarousel(Carousels, 1, "continue")
            .Should()
            .Be(("library_1", "genre_35"));
        HomeCarouselNavigation
            .ForCarousel(Carousels, 2, "continue")
            .Should()
            .Be(("genre_28", "continue"));
        HomeCarouselNavigation.ForContinue(Carousels).Should().Be(("genre_35", "library_1"));
    }

    [Fact]
    public void ForCarousel_WithoutContinueWatching_EndsAreOpen()
    {
        HomeCarouselNavigation.ForCarousel(Carousels, 0, null).Should().Be((null, "genre_28"));
        HomeCarouselNavigation.ForCarousel(Carousels, 2, null).Should().Be(("genre_28", null));
    }

    [Fact]
    public void ForContinue_NoOtherCarousels_HasNoNeighbours()
    {
        HomeCarouselNavigation.ForContinue([]).Should().Be((null, null));
    }
}
