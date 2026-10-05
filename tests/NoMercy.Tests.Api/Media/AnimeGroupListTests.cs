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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Controllers.V1.Media;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using NoMercy.Database;
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

    private static AnimeDemographicWithCountsDto Demographic(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            TvShowsWithVideo = 1,
        };

    private static async Task<List<ComponentEnvelope>> Demographics(
        List<AnimeDemographicWithCountsDto> demographics,
        string? version = null
    )
    {
        Mock<IAnimeDemographicRepository> repository = new();
        repository
            .Setup(r =>
                r.GetDemographicsWithCountsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(demographics);
        AnimeDemographicsController controller = new(repository.Object)
        {
            ControllerContext = SignedIn,
        };

        IActionResult result = await controller.Demographics(new() { Version = version });

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
    public async Task Demographics_AreOneGridOfGroupCards()
    {
        List<ComponentEnvelope> page = await Demographics([
            Demographic(3, "shounen"),
            new() { Id = 4, Name = "empty" },
        ]);

        ComponentEnvelope grid = Assert.Single(page);
        Assert.Equal("NMGrid", grid.Component);
        Assert.Equal("anime-demographics", (string)Props(grid).Id);
        ComponentEnvelope card = Assert.Single(Props(grid).Items);
        Assert.Equal("NMGroupCard", card.Component);
        Assert.Equal(3, Data(card).Id);
        Assert.Equal("Shounen", Data(card).Title);
        Assert.Equal("anime-demographic", Data(card).Type);
        Assert.Equal("/anime/demographics/3", Data(card).Link.ToString());
    }

    [Fact]
    public async Task Demographics_Lolomo_AreLetterRows_WithNeighbors()
    {
        List<ComponentEnvelope> rows = await Demographics(
            [Demographic(1, "shounen"), Demographic(2, "josei"), Demographic(3, "seinen")],
            "lolomo"
        );

        Assert.All(rows, row => Assert.Equal("NMCarousel", row.Component));
        Assert.Equal(["J", "S"], rows.Select(row => Props(row).Title));
        // Neighbors are the adjacent letters, present or not, as on the
        // library and genre routes (LibrariesController.cs:451-456).
        Assert.Equal("I", (string?)Props(rows[0]).PreviousId);
        Assert.Equal("K", (string?)Props(rows[0]).NextId);
        Assert.Equal("R", (string?)Props(rows[1]).PreviousId);
        Assert.Equal("T", (string?)Props(rows[1]).NextId);
        Assert.Equal(["Seinen", "Shounen"], Props(rows[1]).Items.Select(card => Data(card).Title));
        Assert.All(
            rows.SelectMany(row => Props(row).Items),
            card => Assert.Equal("NMGroupCard", card.Component)
        );
    }

    [Fact]
    public async Task Seasons_AreOneGridOfGroupCards()
    {
        List<ComponentEnvelope> page = await Seasons([
            Season(5, 2024, "FALL"),
            new()
            {
                Id = 6,
                Year = 2020,
                Quarter = "WINTER",
            },
        ]);

        ComponentEnvelope grid = Assert.Single(page);
        Assert.Equal("NMGrid", grid.Component);
        Assert.Equal("anime-seasons", (string)Props(grid).Id);
        ComponentEnvelope card = Assert.Single(Props(grid).Items);
        Assert.Equal("NMGroupCard", card.Component);
        Assert.Equal(5, Data(card).Id);
        Assert.Equal(2024, Data(card).Year);
        Assert.Equal("FALL", Data(card).Quarter);
        Assert.Equal("anime-season", Data(card).Type);
        Assert.Equal("/anime/seasons/5", Data(card).Link.ToString());
    }

    // The web card reads every key of a poster's palette: pickPaletteColor
    // walks all six for the band color, and the front poster's gradient takes
    // three (app-web NMGroupCard.vue:75, colorHelper.ts:203-210,
    // useImageStyles.ts:68-71). Anything beyond those six is dead weight on
    // a themes page of ~1600 posters.
    [Fact]
    public async Task GroupPoster_SendsOnlyThePaletteKeysTheCardReads()
    {
        PaletteColors palette = new()
        {
            Dominant = "#111111",
            Primary = "#222222",
            LightVibrant = "#333333",
            DarkVibrant = "#444444",
            LightMuted = "#555555",
            DarkMuted = "#666666",
        };
        List<ComponentEnvelope> page = await Themes([
            new()
            {
                Id = 1,
                Name = "action",
                TvShowsWithVideo = 1,
                ItemPosters = [new("/a.jpg", palette)],
            },
        ]);

        JObject poster = (JObject)
            JObject
                .Parse(JsonConvert.SerializeObject(ComponentResponse.From(page[0])))
                .SelectToken("$.data[0].props.items[0].props.data.item_posters[0]")!;

        Assert.Equal(["src", "color_palette", "color"], poster.Properties().Select(p => p.Name));
        Assert.Equal(
            ["dominant", "primary", "lightVibrant", "darkVibrant", "lightMuted", "darkMuted"],
            ((JObject)poster["color_palette"]!).Properties().Select(p => p.Name)
        );
    }

    // A poster behind the front ones has no palette to send; the key is left
    // out rather than sent as null, so the page of ~1600 posters stays small.
    [Fact]
    public async Task GroupPoster_WithoutPalette_SendsSrcAndColorOnly()
    {
        List<ComponentEnvelope> page = await Themes([
            new()
            {
                Id = 1,
                Name = "action",
                TvShowsWithVideo = 1,
                ItemPosters = [new("/a.jpg", null, "#404040")],
            },
        ]);

        JObject poster = (JObject)
            JObject
                .Parse(JsonConvert.SerializeObject(ComponentResponse.From(page[0])))
                .SelectToken("$.data[0].props.items[0].props.data.item_posters[0]")!;

        Assert.Equal(["src", "color"], poster.Properties().Select(p => p.Name));
        Assert.Equal("#404040", poster["color"]);
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
