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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Controllers.V1.Media;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Authorization;
using NoMercy.Data.DTOs.Specials;
using NoMercy.Data.Repositories;
using NoMercy.Events;
using NoMercy.MediaProcessing.Images;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.Providers.TMDB.Client;
using Xunit;
using MusicAlbumsController = NoMercy.Api.Controllers.V1.Music.AlbumsController;
using MusicArtistsController = NoMercy.Api.Controllers.V1.Music.ArtistsController;
using MusicController = NoMercy.Api.Controllers.V1.Music.MusicController;
using MusicGenresController = NoMercy.Api.Controllers.V1.Music.GenresController;

namespace NoMercy.Tests.Api.Media;

/// <summary>
/// Every version=lolomo page answers exactly one NMLolomo, built from the rows
/// the page held before; every other request keeps its old shape.
/// </summary>
[Trait("Category", "Unit")]
public class LolomoPageTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static ControllerContext SignedIn()
    {
        Mock<IMediaAuthorizationPolicy> policy = new();
        policy.Setup(p => p.IsAllowed(It.IsAny<ClaimsPrincipal?>())).Returns(true);

        ServiceCollection services = new();
        services.AddSingleton(policy.Object);

        return new()
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                User = new(
                    new ClaimsIdentity([new(ClaimTypes.NameIdentifier, UserId.ToString())], "test")
                ),
            },
        };
    }

    private delegate Task<IActionResult> Page(string? version);

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
    ];

    private static Page GenreDetail()
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
        GenresController controller = new(repository.Object) { ControllerContext = SignedIn() };
        return version => controller.Genre(16, new() { Version = version });
    }

    private static Page ThemeDetail()
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
            .ReturnsAsync((new AnimeThemeDetailDto(), Movies, Shows));
        AnimeThemesController controller = new(repository.Object)
        {
            ControllerContext = SignedIn(),
        };
        return version => controller.Theme(29, new() { Version = version });
    }

    private static Page DemographicDetail()
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
            .ReturnsAsync((new AnimeDemographicDetailDto(), Movies, Shows));
        AnimeDemographicsController controller = new(repository.Object)
        {
            ControllerContext = SignedIn(),
        };
        return version => controller.Demographic(1, new() { Version = version });
    }

    private static Page SeasonDetail()
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
            .ReturnsAsync((new AnimeSeasonDetailDto(), Movies, Shows));
        AnimeSeasonsController controller = new(repository.Object)
        {
            ControllerContext = SignedIn(),
        };
        return version => controller.Season(5, new() { Version = version });
    }

    private static Page ThemeList()
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
            .ReturnsAsync([
                new()
                {
                    Id = 1,
                    Name = "Isekai",
                    TvShowsWithVideo = 2,
                },
                new()
                {
                    Id = 2,
                    Name = "Mecha",
                    TvShowsWithVideo = 1,
                },
            ]);
        AnimeThemesController controller = new(repository.Object)
        {
            ControllerContext = SignedIn(),
        };
        return version => controller.Themes(new() { Version = version });
    }

    private static Page DemographicList()
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
            .ReturnsAsync([
                new()
                {
                    Id = 1,
                    Name = "Shonen",
                    TvShowsWithVideo = 2,
                },
                new()
                {
                    Id = 2,
                    Name = "Seinen",
                    TvShowsWithVideo = 1,
                },
            ]);
        AnimeDemographicsController controller = new(repository.Object)
        {
            ControllerContext = SignedIn(),
        };
        return version => controller.Demographics(new() { Version = version });
    }

    private static Page SeasonList()
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
            .ReturnsAsync([
                new()
                {
                    Id = 1,
                    Year = 2024,
                    Quarter = "winter",
                    TvShowsWithVideo = 2,
                },
                new()
                {
                    Id = 2,
                    Year = 2023,
                    Quarter = "fall",
                    TvShowsWithVideo = 1,
                },
            ]);
        AnimeSeasonsController controller = new(repository.Object)
        {
            ControllerContext = SignedIn(),
        };
        return version => controller.Seasons(new() { Version = version });
    }

    private static Page Collections()
    {
        Mock<ICollectionRepository> repository = new();
        repository
            .Setup(r =>
                r.GetCollectionsListAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([
                new()
                {
                    Id = 1,
                    Title = "Akira Saga",
                    TitleSort = "akira saga",
                    ColorPalette = new(),
                },
                new()
                {
                    Id = 2,
                    Title = "Bleach Saga",
                    TitleSort = "bleach saga",
                    ColorPalette = new(),
                },
            ]);
        CollectionsController controller = new(
            repository.Object,
            Mock.Of<ILibraryRepository>(),
            Mock.Of<IJobDispatcher>(),
            Mock.Of<ICollectionMetadataProvider>(),
            NullLogger<CollectionsController>.Instance
        )
        {
            ControllerContext = SignedIn(),
        };
        return version => controller.Collections(new() { Version = version });
    }

    private static Page Library()
    {
        Mock<ILibraryRepository> repository = new();
        repository
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
            .ReturnsAsync([
                new()
                {
                    Id = 1,
                    Title = "Akira",
                    TitleSort = "akira",
                },
                new()
                {
                    Id = 2,
                    Title = "Bambi",
                    TitleSort = "bambi",
                },
            ]);
        repository
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
            .ReturnsAsync([
                new()
                {
                    Id = 3,
                    Title = "Bleach",
                    TitleSort = "bleach",
                },
            ]);
        LibrariesController controller = new(
            repository.Object,
            Mock.Of<ICollectionRepository>(),
            Mock.Of<ISpecialRepository>(),
            Mock.Of<IHomeRepository>(),
            Mock.Of<IUserPlaylistRepository>(),
            Mock.Of<IAnimeThemeRepository>(),
            Mock.Of<IAnimeDemographicRepository>(),
            Mock.Of<IAnimeSeasonRepository>(),
            Mock.Of<IServerConfigurationRepository>(),
            TimeProvider.System
        )
        {
            ControllerContext = SignedIn(),
        };
        return version => controller.Library(Ulid.NewUlid(), new() { Version = version });
    }

    private static Page Specials()
    {
        Mock<ISpecialRepository> repository = new();
        repository
            .Setup(r =>
                r.GetSpecialCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([
                new()
                {
                    Id = Ulid.NewUlid(),
                    Title = "Akira Extras",
                    TitleSort = "akira extras",
                },
                new()
                {
                    Id = Ulid.NewUlid(),
                    Title = "Bleach Extras",
                    TitleSort = "bleach extras",
                },
            ]);
        SpecialController controller = new(
            repository.Object,
            Mock.Of<IJobDispatcher>(),
            NullLogger<SpecialController>.Instance
        )
        {
            ControllerContext = SignedIn(),
        };
        return version => controller.Index(new() { Version = version });
    }

    private static List<AlbumCardDto> AlbumCards =>
        [
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Abbey Road",
                Link = new("/x", UriKind.Relative),
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Blue",
                Link = new("/y", UriKind.Relative),
            },
        ];

    private static List<ArtistCardDto> ArtistCards =>
        [
            new()
            {
                Id = Guid.NewGuid(),
                Name = "ABBA",
                Link = new("/x", UriKind.Relative),
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Bjork",
                Link = new("/y", UriKind.Relative),
            },
        ];

    private static Mock<IMusicRepository> MusicRepository()
    {
        Mock<IMusicRepository> repository = new();
        repository
            .Setup(r => r.GetAllAlbumCardsAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(AlbumCards);
        repository
            .Setup(r =>
                r.GetAlbumCardsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>())
            )
            .ReturnsAsync(AlbumCards);
        repository.Setup(r => r.GetAllArtistCardsAsync(It.IsAny<Guid>())).ReturnsAsync(ArtistCards);
        repository
            .Setup(r => r.GetArtistCardsAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(ArtistCards);
        repository
            .Setup(r => r.GetMusicStartPageAsync(It.IsAny<Guid>()))
            .ReturnsAsync(
                new MusicStartPageData
                {
                    FavoriteArtists = ArtistCards,
                    FavoriteAlbums = AlbumCards,
                    LatestArtists = ArtistCards,
                    LatestAlbums = AlbumCards,
                    LatestGenres =
                    [
                        new()
                        {
                            Id = Guid.NewGuid(),
                            Name = "Rock",
                            Link = new("/z", UriKind.Relative),
                        },
                    ],
                }
            );
        return repository;
    }

    private static MusicAlbumsController Albums() =>
        new(
            NullLogger<MusicAlbumsController>.Instance,
            MusicRepository().Object,
            Mock.Of<IEventBus>(),
            Mock.Of<IJobDispatcher>(),
            Mock.Of<IMusicCoverStore>()
        )
        {
            ControllerContext = SignedIn(),
        };

    private static MusicArtistsController Artists() =>
        new(
            NullLogger<MusicArtistsController>.Instance,
            MusicRepository().Object,
            Mock.Of<IEventBus>(),
            Mock.Of<IJobDispatcher>(),
            Mock.Of<IMusicCoverStore>()
        )
        {
            ControllerContext = SignedIn(),
        };

    private static Page AlbumsAll() =>
        version => Albums().Index("all", new() { Version = version });

    private static Page AlbumsLetter() =>
        version => Albums().Index("a", new() { Version = version });

    private static Page ArtistsAll() =>
        version => Artists().Index("all", new() { Version = version });

    private static Page ArtistsLetter() =>
        version => Artists().Index("a", new() { Version = version });

    private static MusicGenresController MusicGenres()
    {
        List<MusicGenreCardDto> genres =
        [
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Rock",
                Link = new("/r", UriKind.Relative),
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Blues",
                Link = new("/b", UriKind.Relative),
            },
        ];
        Mock<IGenreRepository> repository = new();
        repository.Setup(r => r.GetMusicGenreCardsAsync(It.IsAny<Guid>())).ReturnsAsync(genres);
        repository
            .Setup(r =>
                r.GetPaginatedMusicGenreCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>()
                )
            )
            .ReturnsAsync(genres);
        return new(repository.Object) { ControllerContext = SignedIn() };
    }

    private static Page MusicGenresIndex() =>
        version => MusicGenres().Index(new() { Version = version });

    private static Page MusicGenresByLetter() =>
        version => MusicGenres().LibraryByLetter("r", new() { Version = version });

    private static Page MusicStart() =>
        version =>
            new MusicController(MusicRepository().Object) { ControllerContext = SignedIn() }.Index(
                new() { Version = version }
            );

    public static TheoryData<string> Pages =>
        new()
        {
            { nameof(DemographicList) },
            { nameof(DemographicDetail) },
            { nameof(SeasonList) },
            { nameof(SeasonDetail) },
            { nameof(ThemeList) },
            { nameof(ThemeDetail) },
            { nameof(Collections) },
            { nameof(GenreDetail) },
            { nameof(Library) },
            { nameof(Specials) },
            { nameof(AlbumsAll) },
            { nameof(AlbumsLetter) },
            { nameof(ArtistsAll) },
            { nameof(ArtistsLetter) },
            { nameof(MusicGenresIndex) },
            { nameof(MusicGenresByLetter) },
            { nameof(MusicStart) },
        };

    private static Page ByName(string name) =>
        name switch
        {
            nameof(DemographicList) => DemographicList(),
            nameof(DemographicDetail) => DemographicDetail(),
            nameof(SeasonList) => SeasonList(),
            nameof(SeasonDetail) => SeasonDetail(),
            nameof(ThemeList) => ThemeList(),
            nameof(ThemeDetail) => ThemeDetail(),
            nameof(Collections) => Collections(),
            nameof(GenreDetail) => GenreDetail(),
            nameof(Library) => Library(),
            nameof(Specials) => Specials(),
            nameof(AlbumsAll) => AlbumsAll(),
            nameof(AlbumsLetter) => AlbumsLetter(),
            nameof(ArtistsAll) => ArtistsAll(),
            nameof(ArtistsLetter) => ArtistsLetter(),
            nameof(MusicGenresIndex) => MusicGenresIndex(),
            nameof(MusicGenresByLetter) => MusicGenresByLetter(),
            nameof(MusicStart) => MusicStart(),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

    private static List<ComponentEnvelope> Envelopes(IActionResult result) =>
        [.. Assert.IsType<ComponentResponse>(Assert.IsType<OkObjectResult>(result).Value).Data];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Lolomo_AnswersOneNmLolomo_OfCarousels(string page)
    {
        List<ComponentEnvelope> data = Envelopes(await ByName(page)("lolomo"));

        ComponentEnvelope lolomo = Assert.Single(data);
        Assert.Equal("NMLolomo", lolomo.Component);

        LolomoProps props = Assert.IsType<LolomoProps>(lolomo.Props);
        List<ComponentEnvelope> rows = [.. props.Items];
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal("NMCarousel", row.Component));
        Assert.All(rows, row => Assert.NotEmpty(((ContainerProps)row.Props).Items));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task WithoutVersion_NeverAnswersNmLolomo(string page)
    {
        List<ComponentEnvelope> data = Envelopes(await ByName(page)(null));

        Assert.DoesNotContain(data, envelope => envelope.Component == "NMLolomo");
    }
}
