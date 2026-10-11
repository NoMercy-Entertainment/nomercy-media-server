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

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Controllers.V1.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.DTOs.Specials;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using Xunit;

namespace NoMercy.Tests.Api.Media;

/// <summary>
/// The library overview on mobile and TV shows the anime themes, demographics
/// and seasons as carousels, so a phone or TV has a way to reach those pages.
/// </summary>
[Trait("Category", "Unit")]
public class LibrariesAnimeCarouselTests
{
    private static readonly Ulid LibraryId = Ulid.NewUlid();

    private readonly Mock<ILibraryRepository> _libraries = new()
    {
        DefaultValue = DefaultValue.Empty,
    };
    private readonly Mock<IHomeRepository> _home = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<ICollectionRepository> _collections = new();
    private readonly Mock<ISpecialRepository> _specials = new();
    private readonly Mock<IUserPlaylistRepository> _playlists = new();
    private readonly Mock<IAnimeThemeRepository> _themes = new()
    {
        DefaultValue = DefaultValue.Empty,
    };
    private readonly Mock<IAnimeDemographicRepository> _demographics = new()
    {
        DefaultValue = DefaultValue.Empty,
    };
    private readonly Mock<IAnimeSeasonRepository> _seasons = new()
    {
        DefaultValue = DefaultValue.Empty,
    };
    private readonly Mock<IServerConfigurationRepository> _config = new();

    public LibrariesAnimeCarouselTests()
    {
        _libraries
            .Setup(r => r.GetLibrariesLite(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Library
                {
                    Id = LibraryId,
                    Title = "Anime",
                    Type = "anime",
                },
            ]);
        _libraries
            .Setup(r =>
                r.GetLibraryItemCountsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);
        _libraries
            .Setup(r =>
                r.GetLibraryMovieCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Ulid>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        _libraries
            .Setup(r =>
                r.GetLibraryTvCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Ulid>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            // One show, so the library row has content: a lolomo leaves empty rows out.
            .ReturnsAsync([
                new TvCardDto
                {
                    Id = 1,
                    Title = "Show",
                    NumberOfEpisodes = 1,
                    EpisodesWithVideo = 1,
                },
            ]);
        _collections
            .Setup(r =>
                r.GetCollectionItemCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        _specials
            .Setup(r =>
                r.GetSpecialItemCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            // One special, so a section row has content after the anime rows.
            .ReturnsAsync([
                new SpecialCardDto
                {
                    Id = Ulid.NewUlid(),
                    Title = "Special",
                    NumberOfItems = 1,
                    HaveMovies = 1,
                },
            ]);
        _playlists
            .Setup(r => r.GetUserPlaylistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _home
            .Setup(r =>
                r.GetFavoritesAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new FavoritesData([], [], [], []));
        _themes
            .Setup(r => r.AnyThemedTitlesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _themes
            .Setup(r =>
                r.GetTopThemeIdsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<AnimeCarouselOrder>(),
                    It.IsAny<DateOnly>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([1]);
        _themes
            .Setup(r =>
                r.GetThemesWithCountsByIdsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<int>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([
                new AnimeThemeWithCountsDto
                {
                    Id = 1,
                    Name = "Isekai",
                    TvShowsWithVideo = 3,
                },
            ]);

        _demographics
            .Setup(r =>
                r.GetTopDemographicIdsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<AnimeCarouselOrder>(),
                    It.IsAny<DateOnly>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([2]);
        _demographics
            .Setup(r =>
                r.GetDemographicsWithCountsByIdsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<int>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([
                new AnimeDemographicWithCountsDto
                {
                    Id = 2,
                    Name = "Shounen",
                    TvShowsWithVideo = 2,
                },
            ]);

        _seasons
            .Setup(r =>
                r.GetTopSeasonIdsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<AnimeCarouselOrder>(),
                    It.IsAny<DateOnly>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([3]);
        _seasons
            .Setup(r =>
                r.GetSeasonsWithCountsByIdsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<IReadOnlyList<int>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([
                new AnimeSeasonWithCountsDto
                {
                    Id = 3,
                    Year = 2024,
                    Quarter = "WINTER",
                    TvShowsWithVideo = 1,
                },
            ]);
    }

    private LibrariesController Controller() =>
        new(
            _libraries.Object,
            _collections.Object,
            _specials.Object,
            _home.Object,
            _playlists.Object,
            _themes.Object,
            _demographics.Object,
            _seasons.Object,
            _config.Object,
            TimeProvider.System
        )
        {
            ControllerContext = new()
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
            },
        };

    // The rows of the page: a row page answers one NMLolomo that holds them.
    private static JArray Data(IActionResult result)
    {
        JArray data = (JArray)
            JObject.Parse(JsonConvert.SerializeObject(((OkObjectResult)result).Value))["data"]!;
        JToken? lolomo = data.FirstOrDefault(c => (string?)c["component"] == "NMLolomo");
        return lolomo == null ? data : (JArray)lolomo["props"]!["items"]!;
    }

    // The envelope id is generated; the carousel's own id is in its props.
    private static List<string> Ids(JArray data) =>
        [.. data.Select(c => (string)c["props"]!["id"]!)];

    private static JToken Carousel(JArray data, string id) =>
        data.First(c => (string)c["props"]!["id"]! == id);

    [Fact]
    public async Task Tv_ShowsThemesDemographicsAndSeasons_BetweenLibrariesAndSections()
    {
        List<string> ids = Ids(Data(await Controller().Tv()));

        // Favorites, lists and collections are empty here, and a lolomo leaves empty rows out.
        Assert.Equal(
            [
                $"library_{LibraryId}",
                "anime_themes",
                "anime_demographics",
                "anime_seasons",
                "library_specials",
            ],
            ids
        );
    }

    [Fact]
    public async Task Mobile_ShowsThemesDemographicsAndSeasons_BeforeTheSections()
    {
        List<string> ids = Ids(Data(await Controller().Mobile()));

        int themes = ids.IndexOf("anime_themes");
        Assert.True(themes > 0, string.Join(", ", ids));
        Assert.Equal(
            ["anime_themes", "anime_demographics", "anime_seasons"],
            ids[themes..(themes + 3)]
        );
    }

    [Theory]
    [InlineData("anime_themes", "/anime/themes")]
    [InlineData("anime_demographics", "/anime/demographics")]
    [InlineData("anime_seasons", "/anime/seasons")]
    public async Task EachAnimeCarousel_LinksToItsShowAllPage_AndHoldsGroupCards(
        string id,
        string link
    )
    {
        foreach (
            IActionResult result in new[] { await Controller().Tv(), await Controller().Mobile() }
        )
        {
            JToken carousel = Carousel(Data(result), id);
            Assert.Equal(link, (string)carousel["props"]!["more_link"]!);
            JArray items = (JArray)carousel["props"]!["items"]!;
            Assert.NotEmpty(items);
            Assert.All(
                items,
                item => Assert.Equal(ComponentTypes.GroupCard, (string)item["component"]!)
            );
        }
    }

    [Fact]
    public async Task TheChain_RunsFromTheLastLibraryThroughTheAnimeCarouselsToTheSections()
    {
        JArray data = Data(await Controller().Tv());

        Assert.Equal(
            "anime_themes",
            (string)Carousel(data, $"library_{LibraryId}")["props"]!["next_id"]!
        );
        Assert.Equal(
            $"library_{LibraryId}",
            (string)Carousel(data, "anime_themes")["props"]!["previous_id"]!
        );
        Assert.Equal(
            "library_specials",
            (string)Carousel(data, "anime_seasons")["props"]!["next_id"]!
        );
        Assert.Equal(
            "anime_seasons",
            (string)Carousel(data, "library_specials")["props"]!["previous_id"]!
        );
    }

    [Fact]
    public async Task NoThemedTitles_ShowsNoAnimeCarousels()
    {
        _themes
            .Setup(r => r.AnyThemedTitlesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Assert.DoesNotContain(Ids(Data(await Controller().Tv())), id => id.StartsWith("anime_"));
        Assert.DoesNotContain(
            Ids(Data(await Controller().Mobile())),
            id => id.StartsWith("anime_")
        );
    }

    [Fact]
    public async Task OnlyMusicLibraries_ShowsNoAnimeCarousels()
    {
        _libraries
            .Setup(r => r.GetLibrariesLite(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Library
                {
                    Id = LibraryId,
                    Title = "Music",
                    Type = "music",
                },
            ]);

        Assert.DoesNotContain(Ids(Data(await Controller().Tv())), id => id.StartsWith("anime_"));
    }

    [Fact]
    public async Task ASeededThemeAndDemographicWithoutTitles_AreLeftOutOfTheCarousels()
    {
        _themes
            .Setup(r =>
                r.GetThemesWithCountsByIdsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<int>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([new AnimeThemeWithCountsDto { Id = 1, Name = "Isekai" }]);
        _demographics
            .Setup(r =>
                r.GetDemographicsWithCountsByIdsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<int>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([new AnimeDemographicWithCountsDto { Id = 2, Name = "Shounen" }]);

        JArray data = Data(await Controller().Tv());

        Assert.DoesNotContain("anime_themes", Ids(data));
        Assert.DoesNotContain("anime_demographics", Ids(data));
    }

    [Fact]
    public async Task AnEmptyGroup_IsLeftOut_AndTheChainSkipsIt()
    {
        _demographics
            .Setup(r =>
                r.GetDemographicsWithCountsByIdsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<int>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);

        JArray data = Data(await Controller().Tv());

        Assert.DoesNotContain("anime_demographics", Ids(data));
        Assert.Equal("anime_seasons", (string)Carousel(data, "anime_themes")["props"]!["next_id"]!);
    }

    [Fact]
    public async Task TheServerSetting_PicksTheOrder_AndAMissingSettingUsesTheDefault()
    {
        _config
            .Setup(r =>
                r.GetValueAsync(
                    ServerConfigurationKeys.AnimeCarouselOrderThemes,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync("most_titles");

        await Controller().Tv();

        _themes.Verify(r =>
            r.GetTopThemeIdsAsync(
                It.IsAny<Guid>(),
                AnimeCarouselOrder.MostTitles,
                It.IsAny<DateOnly>(),
                6,
                It.IsAny<CancellationToken>()
            )
        );
        _demographics.Verify(r =>
            r.GetTopDemographicIdsAsync(
                It.IsAny<Guid>(),
                AnimeCarouselOrder.RandomDaily,
                It.IsAny<DateOnly>(),
                6,
                It.IsAny<CancellationToken>()
            )
        );
        _seasons.Verify(r =>
            r.GetTopSeasonIdsAsync(
                It.IsAny<Guid>(),
                AnimeCarouselOrder.Newest,
                It.IsAny<DateOnly>(),
                6,
                It.IsAny<CancellationToken>()
            )
        );
    }
}
