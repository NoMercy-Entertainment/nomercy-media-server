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

using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using Xunit;

namespace NoMercy.Tests.Api.Media;

/// <summary>
/// NMGroupCard is generic: the server sends the title and subtitle as i18n keys
/// with values (the client translates), loadable image URLs, and the icon and
/// accent color.
/// </summary>
[Trait("Category", "Unit")]
public class GroupCardGenericFieldsTests
{
    private static JObject Json(GroupCardData card) =>
        JObject.Parse(JsonConvert.SerializeObject(card));

    private static AnimeSeasonWithCountsDto Season(string quarter, int items = 1) =>
        new()
        {
            Id = 5,
            Year = 2024,
            Quarter = quarter,
            TvShowsWithVideo = items,
        };

    [Theory]
    [InlineData("WINTER", "group.season.winter")]
    [InlineData("SPRING", "group.season.spring")]
    [InlineData("SUMMER", "group.season.summer")]
    [InlineData("FALL", "group.season.fall")]
    public void Season_DisplayTitle_IsTheSeasonKeyWithTheYear(string quarter, string expectedKey)
    {
        JObject card = Json(new GroupCardData(Season(quarter)));

        Assert.Equal(expectedKey, (string?)card["display_title"]!["key"]);
        Assert.Equal(2024, (int?)card["display_title"]!["values"]!["year"]);
    }

    [Fact]
    public void Theme_DisplayTitle_IsTheNameAsKey()
    {
        AnimeThemeWithCountsDto theme = new()
        {
            Id = 7,
            Name = "action",
            TvShowsWithVideo = 1,
        };

        JObject card = Json(new GroupCardData(theme));

        Assert.Equal("Action", (string?)card["display_title"]!["key"]);
        Assert.Empty(card["display_title"]!["values"]!);
    }

    [Fact]
    public void Theme_WithDescription_SendsItAsOverview()
    {
        AnimeThemeWithCountsDto theme = new()
        {
            Id = 7,
            Name = "isekai",
            Description = "Another world.",
            TvShowsWithVideo = 1,
        };

        JObject card = Json(new GroupCardData(theme));

        Assert.Equal("Another world.", (string?)card["overview"]);
    }

    [Fact]
    public void Theme_WithoutDescription_SendsANullOverview()
    {
        AnimeThemeWithCountsDto theme = new() { Id = 7, Name = "isekai" };

        JObject card = Json(new GroupCardData(theme));

        Assert.True(card.ContainsKey("overview"));
        Assert.Equal(JTokenType.Null, card["overview"]!.Type);
    }

    [Fact]
    public void Demographic_And_Season_SendANullOverview()
    {
        JObject demographic = Json(
            new GroupCardData(new AnimeDemographicWithCountsDto { Id = 3, Name = "shounen" })
        );
        JObject season = Json(new GroupCardData(Season("WINTER")));

        Assert.Equal(JTokenType.Null, demographic["overview"]!.Type);
        Assert.Equal(JTokenType.Null, season["overview"]!.Type);
    }

    [Fact]
    public void Demographic_DisplayTitle_IsTheNameAsKey()
    {
        AnimeDemographicWithCountsDto demographic = new()
        {
            Id = 3,
            Name = "shounen",
            TvShowsWithVideo = 1,
        };

        JObject card = Json(new GroupCardData(demographic));

        Assert.Equal("Shounen", (string?)card["display_title"]!["key"]);
        Assert.Empty(card["display_title"]!["values"]!);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void Subtitle_IsTheItemsKeyWithTheCount(int items)
    {
        JObject card = Json(new GroupCardData(Season("WINTER", items)));

        Assert.Equal("group.items", (string?)card["subtitle"]!["key"]);
        Assert.Equal(items, (int?)card["subtitle"]!["values"]!["count"]);
    }

    [Fact]
    public void PosterUrl_IsAbsoluteAndKeepsTheSrcPath()
    {
        AnimeThemeWithCountsDto theme = new()
        {
            Id = 7,
            Name = "action",
            TvShowsWithVideo = 1,
            ItemPosters = [new("/abc123.jpg", null)],
        };

        JObject card = Json(new GroupCardData(theme));

        JToken poster = card["item_posters"]![0]!;
        Assert.Equal("/abc123.jpg", (string?)poster["src"]);
        string url = (string?)poster["url"] ?? string.Empty;
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed), url);
        Assert.StartsWith("http", parsed!.Scheme);
        Assert.Matches(new Regex(@"/tmdb-images/abc123\.jpg\?width=\d+"), url);
    }

    [Fact]
    public void IconAndAccentColor_AreSentOnEveryGroupType()
    {
        AnimeThemeWithCountsDto theme = new()
        {
            Id = 7,
            Name = "action",
            TvShowsWithVideo = 1,
        };
        AnimeDemographicWithCountsDto demographic = new()
        {
            Id = 3,
            Name = "shounen",
            TvShowsWithVideo = 1,
        };

        List<JObject> cards =
        [
            Json(new GroupCardData(theme)),
            Json(new GroupCardData(demographic)),
            Json(new GroupCardData(Season("WINTER"))),
        ];

        foreach (JObject card in cards)
        {
            Assert.False(string.IsNullOrEmpty((string?)card["icon"]));
            Assert.Matches(@"^#[0-9a-fA-F]{6}$", (string?)card["accent_color"] ?? string.Empty);
        }
    }

    [Fact]
    public void Icon_IsChosenByTheServerFromTheGroupName()
    {
        AnimeThemeWithCountsDto theme = new()
        {
            Id = 7,
            Name = "action",
            TvShowsWithVideo = 1,
        };

        JObject card = Json(new GroupCardData(theme));

        Assert.Equal("muscle", (string?)card["icon"]);
    }

    [Fact]
    public void AccentColor_IsTheFrontPostersColor_WhenItHasOne()
    {
        AnimeThemeWithCountsDto theme = new()
        {
            Id = 7,
            Name = "action",
            TvShowsWithVideo = 1,
            ItemPosters = [new("/a.jpg", null, "#aa5500"), new("/b.jpg", null, "#0055aa")],
        };

        JObject card = Json(new GroupCardData(theme));

        Assert.Equal("#aa5500", (string?)card["accent_color"]);
    }

    // Web builds already out read these keys; every one keeps its meaning.
    [Fact]
    public void OldFields_AreUnchanged()
    {
        AnimeSeasonWithCountsDto season = new()
        {
            Id = 5,
            Year = 2024,
            Quarter = "SPRING",
            TvShowsWithVideo = 4,
            TotalTvShows = 6,
            ItemPosters = [new("/a.jpg", null, "#aa5500")],
        };

        JObject card = Json(new GroupCardData(season));

        Assert.Equal(5, (int?)card["id"]);
        Assert.Equal("2024-SPRING", (string?)card["title"]);
        Assert.Equal("2024-2", (string?)card["titleSort"]);
        Assert.Equal("anime-season", (string?)card["type"]);
        Assert.Equal("/anime/seasons/5", (string?)card["link"]);
        Assert.Equal(2024, (int?)card["year"]);
        Assert.Equal("SPRING", (string?)card["quarter"]);
        Assert.Equal(6, (int?)card["number_of_items"]);
        Assert.Equal(4, (int?)card["have_items"]);
        JToken poster = card["item_posters"]![0]!;
        Assert.Equal("/a.jpg", (string?)poster["src"]);
        Assert.Equal("#aa5500", (string?)poster["color"]);
    }
}
