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

using I18N.DotNet;
using Microsoft.EntityFrameworkCore;
using Moq;
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.Services;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using Xunit;

namespace NoMercy.Tests.Api.Media;

// AUD-9843: browse text the server writes follows the request language.
[Trait("Category", "Home")]
public class HomeServiceLocalizedTitlesTests
{
    private static void UseLanguage(string language)
    {
        Localizer localizer = new();
        localizer.LoadXML(typeof(HomeService).Assembly, "Resources.I18N.xml", language);
        LocalizationHelper.UseForCurrentRequest(localizer);
    }

    private static async Task<string> HomeJson()
    {
        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Films",
            Type = MediaTypes.MovieMediaType,
        };

        Mock<IHomeRepository> homeRepository = new();
        homeRepository
            .Setup(r =>
                r.GetHomeParallelDataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>())
            )
            .ReturnsAsync(new HomeParallelData([], [], [library], 0, 1, 0));
        homeRepository
            .Setup(r =>
                r.GetHomeTvsAndMoviesAsync(
                    It.IsAny<List<int>>(),
                    It.IsAny<List<int>>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(new HomeTvsAndMoviesData([], []));

        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository
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
            .ReturnsAsync([new MovieCardDto { Id = 1, Title = "Film" }]);
        libraryRepository
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
            .ReturnsAsync([]);

        HomeService service = new(
            homeRepository.Object,
            libraryRepository.Object,
            new Mock<IDbContextFactory<MediaContext>>().Object
        );

        ComponentResponse response = await service.GetHomeData(Guid.NewGuid(), "nl", "NL");
        return JsonConvert.SerializeObject(response);
    }

    [Fact]
    public async Task GetHomeData_InDutch_HasNoEnglishLibraryRowTitle()
    {
        UseLanguage("nl");

        string json = await HomeJson();

        Assert.DoesNotContain("Latest in", json);
        Assert.Contains("\"title\":\"Nieuw in Films\"", json);
    }

    [Fact]
    public async Task GetHomeData_InEnglish_KeepsTheEnglishLibraryRowTitle()
    {
        UseLanguage("en");

        string json = await HomeJson();

        Assert.Contains("\"title\":\"Latest in Films\"", json);
    }

    [Fact]
    public void LibraryNavigation_InDutch_TranslatesTheMusicPages()
    {
        UseLanguage("nl");

        List<string> labels =
        [
            .. LibraryNavigation.Build([], hasAnime: false, [], []).Select(entry => entry.Label),
        ];

        Assert.Equal(
            ["Start", "Artiesten", "Albums", "Genres", "Nummers die je leuk vindt"],
            labels
        );
    }

    [Fact]
    public void LibraryNavigation_InEnglish_KeepsTheEnglishMusicPages()
    {
        UseLanguage("en");

        List<string> labels =
        [
            .. LibraryNavigation.Build([], hasAnime: false, [], []).Select(entry => entry.Label),
        ];

        Assert.Equal(["Start", "Artists", "Albums", "Genres", "Songs you like"], labels);
    }
}
