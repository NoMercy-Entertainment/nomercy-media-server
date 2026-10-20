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

using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.Services;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;
using Xunit;

namespace NoMercy.Tests.Api.Media;

// The web client posts to /home/continue on every home load and swaps the whole
// carousel for the reply, so the reply has to carry the same prev/next chain the
// first load did: previous is the last carousel on the page, next is the first.
[Trait("Category", "Home")]
public class HomeServiceContinueNavigationTests
{
    private readonly Guid _userId = Guid.NewGuid();

    private readonly Library _libraryWithTitles = new()
    {
        Id = Ulid.NewUlid(),
        Title = "Films",
        Type = MediaTypes.MovieMediaType,
    };

    private readonly Library _libraryWithoutTitles = new()
    {
        Id = Ulid.NewUlid(),
        Title = "Nothing yet",
        Type = MediaTypes.TvMediaType,
    };

    private HomeService BuildService()
    {
        VideoFile file = new()
        {
            Filename = "film.mkv",
            HostFolder = "/x",
            Duration = "01:00:00",
        };
        UserData inProgress = new()
        {
            UserId = _userId,
            MovieId = 1,
            Movie = new() { Id = 1, Title = "Film" },
            VideoFileId = file.Id,
            VideoFile = file,
            Time = 60,
        };

        List<GenreHomeDto> genres =
        [
            new()
            {
                Id = 28,
                Name = "Action",
                MovieIds = [1],
            },
            new()
            {
                Id = 35,
                Name = "Comedy",
                TvIds = [2],
            },
            // A genre whose titles all lack files never becomes a carousel.
            new() { Id = 99, Name = "Western" },
        ];

        Mock<IHomeRepository> homeRepository = new();
        homeRepository
            .Setup(r =>
                r.GetHomeParallelDataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>())
            )
            .ReturnsAsync(
                new HomeParallelData(
                    [inProgress],
                    genres,
                    [_libraryWithTitles, _libraryWithoutTitles],
                    0,
                    1,
                    1
                )
            );
        homeRepository
            .Setup(r =>
                r.GetContinueWatchingAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([inProgress]);
        homeRepository
            .Setup(r =>
                r.GetHomeTvsAndMoviesAsync(
                    It.IsAny<List<int>>(),
                    It.IsAny<List<int>>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(
                new HomeTvsAndMoviesData(
                    [new() { Id = 2, Title = "Show" }],
                    [new() { Id = 1, Title = "Film" }]
                )
            );

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
            .ReturnsAsync([]);
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
        libraryRepository
            .Setup(r =>
                r.GetLibraryMovieCardsAsync(
                    It.IsAny<Guid>(),
                    _libraryWithTitles.Id,
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([new MovieCardDto { Id = 1, Title = "Film" }]);

        return new(
            homeRepository.Object,
            libraryRepository.Object,
            new Mock<IDbContextFactory<MediaContext>>().Object
        );
    }

    private static ContainerProps ContinueCarousel(ComponentResponse response)
    {
        return response
            .Data.Select(envelope => envelope.Props)
            .OfType<ContainerProps>()
            .Single(props => (string)props.Id == "continue");
    }

    [Fact]
    public async Task GetHomeContinueContent_KeepsTheNavigationChainOfTheFirstLoad()
    {
        HomeService service = BuildService();

        ContainerProps onLoad = ContinueCarousel(await service.GetHomeData(_userId, "en", "US"));
        ContainerProps onRefresh = ContinueCarousel(
            await service.GetHomeContinueContent(_userId, "en", "US", Ulid.NewUlid())
        );

        string? loadPrevious = onLoad.PreviousId;
        string? loadNext = onLoad.NextId;
        string? refreshPrevious = onRefresh.PreviousId;
        string? refreshNext = onRefresh.NextId;

        // Pins the chain itself, so the comparison below cannot pass on two empty ends:
        // library rows come first, genre rows last, and empty rows are not on the page.
        loadPrevious.Should().Be("genre_35");
        loadNext.Should().Be($"library_{_libraryWithTitles.Id}");

        refreshNext.Should().Be(loadNext);
        refreshPrevious.Should().Be(loadPrevious);
    }
}
