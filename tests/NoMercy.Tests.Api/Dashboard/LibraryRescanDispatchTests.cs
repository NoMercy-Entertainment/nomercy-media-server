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

using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Media;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Domain;
using Xunit;
using IJobDispatcher = NoMercy.MediaProcessing.Jobs.IJobDispatcher;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// "Rescan files" walked LibraryMovies and LibraryTvs to decide what to dispatch. A music
/// library populates neither, so the endpoint dispatched nothing, returned ok, and the
/// dashboard reported a rescan that never ran.
/// </summary>
[Trait("Category", "Unit")]
public class LibraryRescanDispatchTests
{
    private static LibrariesController BuildController(
        Library library,
        Mock<IJobDispatcher> jobDispatcher
    )
    {
        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository
            .Setup(repository => repository.GetLibraryByIdAsync(library.Id))
            .ReturnsAsync(library);
        libraryRepository
            .Setup(repository => repository.GetAllLibrariesAsync())
            .ReturnsAsync([library]);

        return new(
            libraryRepository.Object,
            Mock.Of<IEncodingPresetRepository>(),
            Mock.Of<IFolderRepository>(),
            jobDispatcher.Object,
            Mock.Of<ILanguageRepository>(),
            Mock.Of<Microsoft.EntityFrameworkCore.IDbContextFactory<NoMercy.Database.MediaContext>>(),
            Mock.Of<NoMercy.Data.Activity.IActivityLogger>(),
            Mock.Of<NoMercy.MediaProcessing.Libraries.IDefaultEncodingPresetLinker>(),
            Mock.Of<NoMercy.MediaProcessing.Files.IFileManager>(),
            Mock.Of<NoMercy.MediaProcessing.Shows.IAnimeClassificationAuditService>(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<LibrariesController>>(),
            Mock.Of<NoMercy.Events.IEventBus>(),
            new NoMercy.Api.Middleware.ServedFolderRegistry()
        );
    }

    private static Library MusicLibrary() =>
        new()
        {
            Id = Ulid.NewUlid(),
            Title = "Music",
            Type = MediaTypes.MusicMediaType,
        };

    [Fact]
    public async Task Rescanning_a_music_library_runs_the_music_scanner_not_the_video_one()
    {
        // FileRescanJob is the video path: pointed at a music library it reports
        // "no parseable candidates" and moves on to deleting video-file and
        // metadata records. The music scanner is LibraryScanJob, which walks the
        // library's folders and matches audio files.
        Library library = MusicLibrary();
        Mock<IJobDispatcher> jobDispatcher = new();

        IActionResult result = await BuildController(library, jobDispatcher).Rescan(library.Id);

        result.Should().BeOfType<OkObjectResult>();
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<LibraryScanJob>(library.Id),
            Times.Once
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(library.Id),
            Times.Never
        );
    }

    [Fact]
    public async Task Rescanning_every_library_reaches_the_music_one_too()
    {
        Library library = MusicLibrary();
        Mock<IJobDispatcher> jobDispatcher = new();

        IActionResult result = await BuildController(library, jobDispatcher).Rescan();

        result.Should().BeOfType<OkObjectResult>();
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<LibraryScanJob>(library.Id),
            Times.Once
        );
    }

    /// <summary>
    /// Music is dispatched once for the library, never per title — the per-title overload
    /// takes an int id that no album or track has.
    /// </summary>
    [Fact]
    public async Task A_music_library_is_never_dispatched_per_title()
    {
        Library library = MusicLibrary();
        Mock<IJobDispatcher> jobDispatcher = new();

        await BuildController(library, jobDispatcher).Rescan(library.Id);

        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(It.IsAny<int>(), It.IsAny<Ulid>()),
            Times.Never
        );
    }

    [Fact]
    public async Task A_movie_library_still_dispatches_one_rescan_per_title()
    {
        Ulid libraryId = Ulid.NewUlid();
        Library library = new()
        {
            Id = libraryId,
            Title = "Films",
            Type = MediaTypes.MovieMediaType,
            LibraryMovies =
            [
                new() { MovieId = 41, LibraryId = libraryId },
                new() { MovieId = 42, LibraryId = libraryId },
            ],
        };
        Mock<IJobDispatcher> jobDispatcher = new();

        await BuildController(library, jobDispatcher).Rescan(libraryId);

        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(41, libraryId),
            Times.Once
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(42, libraryId),
            Times.Once
        );
        jobDispatcher.Verify(
            dispatcher => dispatcher.DispatchJob<FileRescanJob>(It.IsAny<Ulid>()),
            Times.Never
        );
    }

    [Fact]
    public async Task RepairTrackMatches_UnknownLibrary_ReturnsNotFound()
    {
        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository
            .Setup(repository => repository.GetLibraryByIdAsync(It.IsAny<Ulid>()))
            .ReturnsAsync((Library?)null);

        LibrariesController controller = new(
            libraryRepository.Object,
            Mock.Of<IEncodingPresetRepository>(),
            Mock.Of<IFolderRepository>(),
            Mock.Of<IJobDispatcher>(),
            Mock.Of<ILanguageRepository>(),
            Mock.Of<Microsoft.EntityFrameworkCore.IDbContextFactory<NoMercy.Database.MediaContext>>(),
            Mock.Of<NoMercy.Data.Activity.IActivityLogger>(),
            Mock.Of<NoMercy.MediaProcessing.Libraries.IDefaultEncodingPresetLinker>(),
            Mock.Of<NoMercy.MediaProcessing.Files.IFileManager>(),
            Mock.Of<NoMercy.MediaProcessing.Shows.IAnimeClassificationAuditService>(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<LibrariesController>>(),
            Mock.Of<NoMercy.Events.IEventBus>(),
            new NoMercy.Api.Middleware.ServedFolderRegistry()
        );

        IActionResult result = await controller.RepairTrackMatches(Ulid.NewUlid());

        ObjectResult objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(404);
    }

    /// <summary>
    /// The matcher bug this endpoint repairs is specific to the audio import path
    /// (<c>AudioImportJob</c>) — a video library was never routed through it, so
    /// running the repair there could only ever be a mistake, not a real request.
    /// </summary>
    [Fact]
    public async Task RepairTrackMatches_NonMusicLibrary_ReturnsBadRequest()
    {
        Ulid libraryId = Ulid.NewUlid();
        Library library = new()
        {
            Id = libraryId,
            Title = "Films",
            Type = MediaTypes.MovieMediaType,
        };
        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository
            .Setup(repository => repository.GetLibraryByIdAsync(libraryId))
            .ReturnsAsync(library);

        LibrariesController controller = new(
            libraryRepository.Object,
            Mock.Of<IEncodingPresetRepository>(),
            Mock.Of<IFolderRepository>(),
            Mock.Of<IJobDispatcher>(),
            Mock.Of<ILanguageRepository>(),
            Mock.Of<Microsoft.EntityFrameworkCore.IDbContextFactory<NoMercy.Database.MediaContext>>(),
            Mock.Of<NoMercy.Data.Activity.IActivityLogger>(),
            Mock.Of<NoMercy.MediaProcessing.Libraries.IDefaultEncodingPresetLinker>(),
            Mock.Of<NoMercy.MediaProcessing.Files.IFileManager>(),
            Mock.Of<NoMercy.MediaProcessing.Shows.IAnimeClassificationAuditService>(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<LibrariesController>>(),
            Mock.Of<NoMercy.Events.IEventBus>(),
            new NoMercy.Api.Middleware.ServedFolderRegistry()
        );

        IActionResult result = await controller.RepairTrackMatches(libraryId);

        ObjectResult objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
    }
}
