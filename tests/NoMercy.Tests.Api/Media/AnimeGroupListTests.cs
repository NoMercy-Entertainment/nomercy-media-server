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
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Unit")]
public class AnimeGroupListTests
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

    private static AnimeThemeWithCountsDto Theme(int id, string name, params string[] posters) =>
        new()
        {
            Id = id,
            Name = name,
            TvShowsWithVideo = 1,
            ItemPosters = [.. posters.Select(path => new GroupPoster(path, null))],
        };

    private static AnimeSeasonWithCountsDto Season(int id, int year, string quarter) =>
        new()
        {
            Id = id,
            Year = year,
            Quarter = quarter,
            TvShowsWithVideo = 1,
        };

    private static async Task<List<ComponentEnvelope>> Themes(
        List<AnimeThemeWithCountsDto> themes,
        string? version = null
    )
    {
        Mock<IAnimeThemeRepository> repository = new();
        repository
            .Setup(r =>
                r.GetThemesWithCountsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(themes);
        AnimeThemesController controller = new(repository.Object) { ControllerContext = SignedIn };

        IActionResult result = await controller.Themes(new() { Version = version });

        return
        [
            .. Assert.IsType<ComponentResponse>(Assert.IsType<OkObjectResult>(result).Value).Data,
        ];
    }

    private static async Task<List<ComponentEnvelope>> Seasons(
        List<AnimeSeasonWithCountsDto> seasons,
        string? version = null
    )
    {
        Mock<IAnimeSeasonRepository> repository = new();
        repository
            .Setup(r =>
                r.GetSeasonsWithCountsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(seasons);
        AnimeSeasonsController controller = new(repository.Object) { ControllerContext = SignedIn };

        IActionResult result = await controller.Seasons(new() { Version = version });

        return
        [
            .. Assert.IsType<ComponentResponse>(Assert.IsType<OkObjectResult>(result).Value).Data,
        ];
    }

    private static ContainerProps Props(ComponentEnvelope envelope) =>
        (ContainerProps)envelope.Props;

    private static GroupCardData Data(ComponentEnvelope card) =>
        ((LeafProps<GroupCardData>)card.Props).Data!;

    [Fact]
    public async Task Themes_AreOneGridOfGroupCards_WithTheirPosters()
    {
        List<ComponentEnvelope> page = await Themes([Theme(7, "action", "/a.jpg", "/b.jpg")]);

        ComponentEnvelope grid = Assert.Single(page);
        Assert.Equal("NMGrid", grid.Component);
        Assert.Equal("anime-themes", (string)Props(grid).Id);
        ComponentEnvelope card = Assert.Single(Props(grid).Items);
        Assert.Equal("NMGroupCard", card.Component);
        Assert.Equal(7, Data(card).Id);
        Assert.Equal("Action", Data(card).Title);
        Assert.Equal("anime-theme", Data(card).Type);
        Assert.Equal("/anime/themes/7", Data(card).Link.ToString());
        Assert.Equal(["/a.jpg", "/b.jpg"], Data(card).ItemPosters.Select(poster => poster.Src));
    }

    // Only groups the user can play show (baa4feaca).
    [Fact]
    public async Task Themes_WithoutAnythingToPlay_AreLeftOut()
    {
        List<ComponentEnvelope> page = await Themes([
            Theme(1, "action"),
            new() { Id = 2, Name = "empty" },
        ]);

        Assert.Equal([1], Props(Assert.Single(page)).Items.Select(card => Data(card).Id));
    }

    // The lolomo switch gives letter rows like the library and genre routes.
    [Fact]
    public async Task Themes_Lolomo_AreLetterRows_WithNeighbors()
    {
        List<ComponentEnvelope> rows = await Themes(
            [
                Theme(1, "comedy"),
                Theme(2, "action"),
                Theme(3, "adventure"),
                Theme(4, "2.5 dimensional"),
            ],
            "lolomo"
        );

        Assert.All(rows, row => Assert.Equal("NMCarousel", row.Component));
        Assert.Equal(["#", "A", "C"], rows.Select(row => Props(row).Title));
        Assert.Equal(["#", "A", "C"], rows.Select(row => (string)Props(row).Id));
        Assert.Null(Props(rows[0]).PreviousId);
        Assert.Equal("A", (string?)Props(rows[0]).NextId);
        Assert.Equal(
            ["Action", "Adventure"],
            Props(rows[1]).Items.Select(card => Data(card).Title)
        );
        Assert.All(
            rows.SelectMany(row => Props(row).Items),
            card => Assert.Equal("NMGroupCard", card.Component)
        );
    }

    [Fact]
    public async Task Seasons_Lolomo_AreYearRows_NewestFirst_QuartersInOrder()
    {
        List<ComponentEnvelope> rows = await Seasons(
            [
                Season(1, 2023, "SPRING"),
                Season(2, 2024, "FALL"),
                Season(3, 2024, "WINTER"),
                Season(4, 2024, "SUMMER"),
            ],
            "lolomo"
        );

        Assert.Equal(["2024", "2023"], rows.Select(row => Props(row).Title));
        Assert.Null(Props(rows[0]).PreviousId);
        Assert.Equal("2023", (string?)Props(rows[0]).NextId);
        Assert.Equal(
            ["WINTER", "SUMMER", "FALL"],
            Props(rows[0]).Items.Select(card => Data(card).Quarter)
        );
    }

    // The cached answer must never cross shapes (grid vs lolomo).
    [Theory]
    [InlineData(typeof(AnimeThemesController), "Themes")]
    [InlineData(typeof(AnimeDemographicsController), "Demographics")]
    [InlineData(typeof(AnimeSeasonsController), "Seasons")]
    public void ListEndpoint_CacheVariesByVersion(Type controller, string action)
    {
        ResponseCacheAttribute cache = controller
            .GetMethod(action)!
            .GetCustomAttribute<ResponseCacheAttribute>()!;

        Assert.Contains("version", cache.VaryByQueryKeys!);
        Assert.True(string.IsNullOrEmpty(cache.VaryByHeader));
    }
}
