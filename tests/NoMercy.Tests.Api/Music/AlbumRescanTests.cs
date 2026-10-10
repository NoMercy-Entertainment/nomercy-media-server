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
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Api.Controllers.V1.Music;
using NoMercy.Api.DTOs.Common;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Music;
using NoMercy.Events;
using NoMercy.MediaProcessing.Images;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using Xunit;

namespace NoMercy.Tests.Api.Music;

[Trait("Category", "Unit")]
public class AlbumRescanTests
{
    private static AlbumsController BuildController(
        Mock<IMusicRepository> repository,
        Mock<IJobDispatcher> dispatcher
    ) =>
        new(
            Mock.Of<ILogger<AlbumsController>>(),
            repository.Object,
            Mock.Of<IEventBus>(),
            dispatcher.Object,
            Mock.Of<IMusicCoverStore>()
        );

    [Fact]
    public async Task Unknown_album_returns_not_found_without_queueing()
    {
        Guid id = Guid.NewGuid();
        Mock<IMusicRepository> repository = new();
        Mock<IJobDispatcher> dispatcher = new();

        IActionResult result = await BuildController(repository, dispatcher).Rescan(id);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
        dispatcher.Verify(
            job =>
                job.DispatchJob<AudioImportJob>(
                    It.IsAny<Ulid>(),
                    It.IsAny<Ulid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<string>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task Known_album_queues_its_folder_scan_and_reports_success()
    {
        Album album = new()
        {
            Id = Guid.NewGuid(),
            LibraryId = Ulid.NewUlid(),
            FolderId = Ulid.NewUlid(),
            HostFolder = "music/album",
        };
        Mock<IMusicRepository> repository = new();
        repository
            .Setup(data => data.GetAlbumWithLibraryFolderAsync(album.Id, default))
            .ReturnsAsync(album);
        Mock<IJobDispatcher> dispatcher = new();

        IActionResult result = await BuildController(repository, dispatcher).Rescan(album.Id);

        result.Should().BeOfType<OkObjectResult>();
        dispatcher.Verify(
            job =>
                job.DispatchJob<AudioImportJob>(
                    album.LibraryId,
                    album.FolderId,
                    album.Id,
                    album.HostFolder
                ),
            Times.Once
        );
        StatusResponseDto<string> response = result
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeOfType<StatusResponseDto<string>>()
            .Which;
        response.Status.Should().Be("ok");
        response.Message.Should().Be("Rescan started");
    }
}
