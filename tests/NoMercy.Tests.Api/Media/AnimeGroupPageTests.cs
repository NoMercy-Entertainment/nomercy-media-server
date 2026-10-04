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
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.Controllers.V1.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.Services;
using NoMercy.Data.Repositories;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Unit")]
public class AnimeGroupPageTests
{
    private static (GenreCardData Card, string[] Posters) Theme(int id, string name, params string[] posters) =>
        (new GenreCardData(new AnimeThemeWithCountsDto { Id = id, Name = name, TvShowsWithVideo = 1 }), posters);

    private static (GenreCardData Card, string[] Posters) Season(int id, int year, string quarter) =>
        (
            new GenreCardData(
                new AnimeSeasonWithCountsDto { Id = id, Year = year, Quarter = quarter, TvShowsWithVideo = 1 }
            ),
            []
        );

    private static ContainerProps Props(ComponentEnvelope envelope) => (ContainerProps)envelope.Props;

    private static List<(GenreCardData, string[])> Themes(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Theme(i, $"{(char)('A' + i % 26)}theme {i}"))];

    [Theory]
    [InlineData("NMGroupCard", true)]
    [InlineData("NMFutureCard, NMGroupCard", true)]
    [InlineData(" NMGroupCard ,NMOther", true)]
    [InlineData("NMGroupCardX", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Draws_ReadsTheCommaListTheAppSends(string? header, bool expected)
    {
        Assert.Equal(expected, AnimeGroupPage.Draws(header, "NMGroupCard"));
    }

    // An app that does not list NMGroupCard gets exactly today's page: one grid of genre cards.
    [Fact]
    public void Grid_WithoutTheOptIn_IsTodaysGenreCardGrid()
    {
        ComponentResponse page = AnimeGroupPage.Build(
            "anime-themes",
            [Theme(1, "action", "/a.jpg")],
            AnimeGroupRows.Letter,
            lolomo: false,
            groupCard: false
        );

        ComponentEnvelope grid = Assert.Single(page.Data);
        Assert.Equal("NMGrid", grid.Component);
        Assert.Equal("anime-themes", (string)Props(grid).Id);
        ComponentEnvelope card = Assert.Single(Props(grid).Items);
        Assert.Equal("NMGenreCard", card.Component);
        Assert.IsType<LeafProps<GenreCardData>>(card.Props);
    }

    [Fact]
    public void Grid_WithTheOptIn_SendsGroupCardsWithTheirPosters()
    {
        ComponentResponse page = AnimeGroupPage.Build(
            "anime-themes",
            [Theme(7, "action", "/a.jpg", "/b.jpg")],
            AnimeGroupRows.Letter,
            lolomo: false,
            groupCard: true
        );

        ComponentEnvelope card = Assert.Single(Props(Assert.Single(page.Data)).Items);
        Assert.Equal("NMGroupCard", card.Component);
        GroupCardData data = ((LeafProps<GroupCardData>)card.Props).Data!;
        Assert.Equal(7, data.Id);
        Assert.Equal("Action", data.Title);
        Assert.Equal("anime-theme", data.Type);
        Assert.Equal("/anime/themes/7", data.Link.ToString());
        Assert.Equal(["/a.jpg", "/b.jpg"], data.ItemPosters);
    }

    // A page that fits in one row is one row: splitting a handful of groups only adds empty steps.
    [Fact]
    public void Lolomo_ThatFitsOneRow_IsOneRow()
    {
        ComponentResponse page = AnimeGroupPage.Build(
            "anime-demographics",
            Themes(AnimeGroupPage.RowMax),
            AnimeGroupRows.Letter,
            lolomo: true,
            groupCard: true
        );

        ComponentEnvelope row = Assert.Single(page.Data);
        Assert.Equal("NMCarousel", row.Component);
        Assert.Equal(AnimeGroupPage.RowMax, Props(row).Items.Count());
    }

    [Fact]
    public void Lolomo_OverTheMax_SplitsByLetter_InAlphabetOrder_WithNeighbors()
    {
        List<(GenreCardData, string[])> themes =
        [
            .. Themes(AnimeGroupPage.RowMax),
            Theme(900, "2.5 dimensional"),
        ];

        List<ComponentEnvelope> rows = [.. AnimeGroupPage.Build("anime-themes", themes, AnimeGroupRows.Letter, lolomo: true, groupCard: true).Data];

        Assert.All(rows, row => Assert.Equal("NMCarousel", row.Component));
        Assert.Equal(["#", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T"], rows.Select(row => Props(row).Title));
        Assert.Null(Props(rows[0]).PreviousId);
        Assert.Equal("A", (string)Props(rows[0]).NextId);
        Assert.Equal("#", (string)Props(rows[1]).PreviousId);
        Assert.Null(Props(rows[^1]).NextId);
        Assert.Equal(themes.Count, rows.Sum(row => Props(row).Items.Count()));
    }

    [Fact]
    public void Lolomo_Seasons_SplitByYear_NewestFirst_QuartersInOrder()
    {
        List<(GenreCardData, string[])> seasons =
        [
            .. Enumerable.Range(0, AnimeGroupPage.RowMax).Select(i => Season(i, 2000 + i / 4, "WINTER")),
            Season(100, 2024, "FALL"),
            Season(101, 2024, "WINTER"),
            Season(102, 2024, "SUMMER"),
        ];

        List<ComponentEnvelope> rows = [.. AnimeGroupPage.Build("anime-seasons", seasons, AnimeGroupRows.YearDescending, lolomo: true, groupCard: false).Data];

        Assert.Equal("2024", Props(rows[0]).Title);
        Assert.Equal(["2024", "2004", "2003", "2002", "2001", "2000"], rows.Select(row => Props(row).Title));
        Assert.Equal(
            ["WINTER", "SUMMER", "FALL"],
            Props(rows[0]).Items.Select(card => ((LeafProps<GenreCardData>)card.Props).Data!.Quarter)
        );
    }

    // The cached answer must never cross shapes (grid vs lolomo) or card types (old vs new apps).
    [Theory]
    [InlineData(typeof(AnimeThemesController), "Themes")]
    [InlineData(typeof(AnimeDemographicsController), "Demographics")]
    [InlineData(typeof(AnimeSeasonsController), "Seasons")]
    public void ListEndpoint_CacheVariesByVersionAndTheComponentsHeader(Type controller, string action)
    {
        ResponseCacheAttribute cache = controller.GetMethod(action)!.GetCustomAttribute<ResponseCacheAttribute>()!;

        Assert.Contains("version", cache.VaryByQueryKeys!);
        Assert.Equal(AnimeGroupPage.ComponentsHeader, cache.VaryByHeader);
    }
}
