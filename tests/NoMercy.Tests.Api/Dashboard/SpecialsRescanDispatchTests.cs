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

using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Media;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using Xunit;
using IJobDispatcher = NoMercy.MediaProcessing.Jobs.IJobDispatcher;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// Rescanning a special went through LibraryLogic, which looks the id up in Libraries.
/// A special's id never matches a library, so the per-special rescan always answered
/// "Special not found" and rescan-all dispatched nothing while reporting ok. A special
/// is a list of movies and episodes; rescanning it means the FileRescanJob the movie
/// and show rescans already use, once per movie and once per show.
/// </summary>
[Trait("Category", "Unit")]
public class SpecialsRescanDispatchTests
{
    private static readonly Ulid MovieLibraryId = Ulid.NewUlid();
    private static readonly Ulid TvLibraryId = Ulid.NewUlid();

    private static SpecialItem MovieItem(int movieId) =>
        new()
        {
            MovieId = movieId,
            Movie = new() { Id = movieId, LibraryId = MovieLibraryId },
        };

    private static SpecialItem EpisodeItem(int episodeId, int tvId) =>
        new()
        {
            EpisodeId = episodeId,
            Episode = new()
            {
                Id = episodeId,
                TvId = tvId,
                Tv = new() { Id = tvId, LibraryId = TvLibraryId },
            },
        };

    private static Special SpecialWith(string title, params SpecialItem[] items) =>
        new()
        {
            Id = Ulid.NewUlid(),
            Title = title,
            Items = items,
        };

    private static SpecialsController BuildController(
        List<Special> specials,
        Mock<IJobDispatcher> jobDispatcher
    )
    {
        Mock<ISpecialRepository> specialRepository = new();
        specialRepository
            .Setup(repository =>
                repository.GetAllSpecialsForRescanAsync(It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(specials);
        specialRepository
            .Setup(repository =>
                repository.GetSpecialForRescanAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                (Ulid id, CancellationToken _) => specials.FirstOrDefault(s => s.Id == id)
            );

        return new(specialRepository.Object, jobDispatcher.Object);
    }

    [Fact]
    public async Task Rescanning_a_special_dispatches_a_file_rescan_for_each_of_its_items()
    {
        Special special = SpecialWith("Marathon", MovieItem(41), EpisodeItem(7, 1399));
        Mock<IJobDispatcher> jobDispatcher = new();

        IActionResult result = await BuildController([special], jobDispatcher).Rescan(special.Id);

        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(41, MovieLibraryId),
            Times.Once,
            "the special's movie must get the same file rescan the movie page dispatches"
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(1399, TvLibraryId),
            Times.Once,
            "the special's episode must get the file rescan of its show"
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(It.IsAny<int>(), It.IsAny<Ulid>()),
            Times.Exactly(2)
        );
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Rescanning_every_special_dispatches_a_file_rescan_for_each_item_of_each_special()
    {
        List<Special> specials =
        [
            SpecialWith("Marathon", MovieItem(41), EpisodeItem(7, 1399)),
            SpecialWith("Directors cut", MovieItem(42), EpisodeItem(9, 1402)),
        ];
        Mock<IJobDispatcher> jobDispatcher = new();

        IActionResult result = await BuildController(specials, jobDispatcher).RescanAll();

        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(41, MovieLibraryId),
            Times.Once
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(1399, TvLibraryId),
            Times.Once
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(42, MovieLibraryId),
            Times.Once
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(1402, TvLibraryId),
            Times.Once
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(It.IsAny<int>(), It.IsAny<Ulid>()),
            Times.Exactly(4)
        );
        result.Should().BeOfType<OkObjectResult>();
    }

    /// <summary>
    /// FileRescanJob is addressed per show, not per episode, so two episodes of one show
    /// are one rescan.
    /// </summary>
    [Fact]
    public async Task Two_episodes_of_one_show_are_rescanned_once()
    {
        Special special = SpecialWith("Two-parter", EpisodeItem(7, 1399), EpisodeItem(8, 1399));
        Mock<IJobDispatcher> jobDispatcher = new();

        await BuildController([special], jobDispatcher).Rescan(special.Id);

        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(1399, TvLibraryId),
            Times.Once
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(It.IsAny<int>(), It.IsAny<Ulid>()),
            Times.Once
        );
    }

    [Fact]
    public async Task A_movie_in_two_specials_is_rescanned_once_by_rescan_all()
    {
        List<Special> specials =
        [
            SpecialWith("Marathon", MovieItem(41)),
            SpecialWith("Directors cut", MovieItem(41), MovieItem(42)),
        ];
        Mock<IJobDispatcher> jobDispatcher = new();

        await BuildController(specials, jobDispatcher).RescanAll();

        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(41, MovieLibraryId),
            Times.Once
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(42, MovieLibraryId),
            Times.Once
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(It.IsAny<int>(), It.IsAny<Ulid>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task Rescanning_an_unknown_special_is_not_found()
    {
        Mock<IJobDispatcher> jobDispatcher = new();

        IActionResult result = await BuildController([], jobDispatcher).Rescan(Ulid.NewUlid());

        ObjectResult objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(404);
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(It.IsAny<int>(), It.IsAny<Ulid>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Rescanning_every_special_with_none_present_is_not_found()
    {
        Mock<IJobDispatcher> jobDispatcher = new();

        IActionResult result = await BuildController([], jobDispatcher).RescanAll();

        ObjectResult objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(404);
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(It.IsAny<int>(), It.IsAny<Ulid>()),
            Times.Never
        );
    }
}
