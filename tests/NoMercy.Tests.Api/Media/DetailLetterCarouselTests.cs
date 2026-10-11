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

using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Media;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using Xunit;

namespace NoMercy.Tests.Api.Media;

/// <summary>
/// A genre detail page answers version=lolomo with letter carousels in one
/// container; a single anime theme, demographic or season answers it with one
/// carousel. Every other request gets one grid, like every other route.
/// </summary>
[Trait("Category", "Unit")]
public class DetailLetterCarouselTests
{
    private static readonly ControllerContext SignedIn = new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new(
                new ClaimsIdentity(
                    [new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                    "test"
                )
            ),
        },
    };

    private static readonly List<HomeMovieCardDto> Movies =
    [
        new()
        {
            Id = 1,
            Title = "Akira",
            TitleSort = "akira",
        },
        new()
        {
            Id = 2,
            Title = "9 Souls",
            TitleSort = "9 souls",
        },
    ];

    private static readonly List<HomeTvCardDto> Shows =
    [
        new()
        {
            Id = 3,
            Title = "Bleach",
            TitleSort = "bleach",
        },
        new()
        {
            Id = 4,
            Title = "Ao Ashi",
            TitleSort = "ao ashi",
        },
    ];

    private static readonly string[] AllTitleSorts = ["9 souls", "akira", "ao ashi", "bleach"];

    private delegate Task<IActionResult> Detail(string? version);

    private static Detail Genre()
    {
        Mock<IGenreRepository> repository = new();
        repository
            .Setup(r =>
                r.GetGenreCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((new GenreDetailDto(), Movies, Shows));
        GenresController controller = new(repository.Object) { ControllerContext = SignedIn };
        return version => controller.Genre(16, new() { Version = version });
    }

    private static Detail Theme()
    {
        Mock<IAnimeThemeRepository> repository = new();
        repository
            .Setup(r =>
                r.GetThemeCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((new AnimeThemeDetailDto { Name = "Isekai" }, Movies, Shows));
        AnimeThemesController controller = new(repository.Object) { ControllerContext = SignedIn };
        return version => controller.Theme(29, new() { Version = version });
    }

    private static Detail Demographic()
    {
        Mock<IAnimeDemographicRepository> repository = new();
        repository
            .Setup(r =>
                r.GetDemographicCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (
                    new AnimeDemographicDetailDto { Name = "Shounen", TranslatedName = "Shōnen" },
                    Movies,
                    Shows
                )
            );
        AnimeDemographicsController controller = new(repository.Object)
        {
            ControllerContext = SignedIn,
        };
        return version => controller.Demographic(1, new() { Version = version });
    }

    private static Detail Season()
    {
        Mock<IAnimeSeasonRepository> repository = new();
        repository
            .Setup(r =>
                r.GetSeasonCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (new AnimeSeasonDetailDto { Year = 2024, Quarter = "winter" }, Movies, Shows)
            );
        AnimeSeasonsController controller = new(repository.Object) { ControllerContext = SignedIn };
        return version => controller.Season(5, new() { Version = version });
    }

    public static TheoryData<string, string> CarouselPages =>
        new() { { "Genre", "genre-carousels" } };

    // Stoney, 2026-10-07: "a single anime theme or year should not have an
    // alphabetical split, it should be one row. same for single demographics".
    // Stoney, 2026-10-07: "the theme groups need the split while the individual
    // group needs just one carousel ... the carousel does need its header because
    // its now not clear what anime grouping we are seeing".
    public static TheoryData<string, string, string> SingleRowPages =>
        new()
        {
            { "Theme", "anime-theme-carousels", "Isekai" },
            { "Demographic", "anime-demographic-carousels", "Shōnen" },
            { "Season", "anime-season-carousels", "Winter 2024" },
        };

    public static TheoryData<string, string> GridPages =>
        new()
        {
            { "Genre", "genre-items" },
            { "Theme", "anime-theme-items" },
            { "Demographic", "anime-demographic-items" },
            { "Season", "anime-season-items" },
        };

    private static Detail Page(string action) =>
        action switch
        {
            "Genre" => Genre(),
            "Theme" => Theme(),
            "Demographic" => Demographic(),
            _ => Season(),
        };

    private static ComponentEnvelope Envelope(IActionResult result) =>
        Assert.Single(
            Assert.IsType<ComponentResponse>(Assert.IsType<OkObjectResult>(result).Value).Data
        );

    private static ContainerProps Props(ComponentEnvelope envelope) =>
        (ContainerProps)envelope.Props;

    [Theory]
    [MemberData(nameof(CarouselPages))]
    public async Task Detail_Lolomo_IsOneNmLolomoOfLetterCarousels_HoldingEveryTitle(
        string action,
        string carouselsId
    )
    {
        ComponentEnvelope lolomo = Envelope(await Page(action)("lolomo"));

        Assert.Equal("NMLolomo", lolomo.Component);
        Assert.Equal(carouselsId, (string)Assert.IsType<LolomoProps>(lolomo.Props).Id);

        List<ComponentEnvelope> rows = [.. ((LolomoProps)lolomo.Props).Items];
        Assert.All(rows, row => Assert.Equal("NMCarousel", row.Component));
        Assert.Equal(["#", "A", "B"], rows.Select(row => (string)Props(row).Id));
        Assert.Equal(["#", "A", "B"], rows.Select(row => Props(row).Title));

        List<string> titleSorts =
        [
            .. rows.SelectMany(row => Props(row).Items)
                .Select(card => ((LeafProps<CardData>)card.Props).Data!.TitleSort)
                .Order(),
        ];
        Assert.Equal(AllTitleSorts, titleSorts);
    }

    [Theory]
    [MemberData(nameof(SingleRowPages))]
    public async Task Detail_Lolomo_IsOneNmLolomoOfOneCarousel_HoldingEveryTitleInOrder(
        string action,
        string carouselsId,
        string header
    )
    {
        ComponentEnvelope lolomo = Envelope(await Page(action)("lolomo"));

        Assert.Equal("NMLolomo", lolomo.Component);
        Assert.Equal(carouselsId, (string)Assert.IsType<LolomoProps>(lolomo.Props).Id);

        ComponentEnvelope row = Assert.Single(((LolomoProps)lolomo.Props).Items);
        Assert.Equal("NMCarousel", row.Component);
        Assert.Equal(header, Props(row).Title);
        Assert.Equal(
            AllTitleSorts,
            Props(row).Items.Select(card => ((LeafProps<CardData>)card.Props).Data!.TitleSort)
        );
    }

    [Theory]
    [MemberData(nameof(GridPages))]
    public async Task Detail_WithoutLolomo_StaysOneGrid(string action, string gridId)
    {
        ComponentEnvelope grid = Envelope(await Page(action)(null));

        Assert.Equal("NMGrid", grid.Component);
        Assert.Equal(gridId, (string)Props(grid).Id);
        Assert.Equal(4, Props(grid).Items.Count());
    }

    // The cached answer must never cross shapes (grid vs lolomo).
    [Theory]
    [InlineData(typeof(AnimeThemesController), "Theme")]
    [InlineData(typeof(AnimeDemographicsController), "Demographic")]
    [InlineData(typeof(AnimeSeasonsController), "Season")]
    public void DetailEndpoint_CacheVariesByVersion(Type controller, string action)
    {
        ResponseCacheAttribute cache = controller
            .GetMethod(action)!
            .GetCustomAttribute<ResponseCacheAttribute>()!;

        Assert.Contains("version", cache.VaryByQueryKeys!);
    }
}
