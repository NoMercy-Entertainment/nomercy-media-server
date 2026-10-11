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

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Admin;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Encoder;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

[Trait("Category", "IncompleteEncodes")]
public class RetryIncompleteEncodeDispatchTests
{
    private static TasksController BuildController(
        IIncompleteEncodeRepository repository,
        IJobDispatcher dispatcher
    ) =>
        new(
            null!,
            null!,
            null!,
            null!,
            repository,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            dispatcher,
            null!
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retry_RemovesRowOnlyWhenDispatchSucceeds(bool queueAvailable)
    {
        Ulid folderId = Ulid.NewUlid();
        Ulid libraryId = Ulid.NewUlid();
        IncompleteEncode row = new()
        {
            Id = 42,
            FolderId = folderId.ToString(),
            MediaId = 123,
            Title = "Movie",
            MissingRenditions = "video/h264/1080p",
            AttemptsMade = 1,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        };
        Mock<IIncompleteEncodeRepository> repository = new();
        repository.Setup(r => r.FindAsync(row.Id)).ReturnsAsync(row);
        repository
            .Setup(r => r.FindFolderLibraryAsync(folderId))
            .ReturnsAsync(new FolderLibrary(folderId, libraryId));
        repository
            .Setup(r => r.FindVideoFileForMediaAsync(123))
            .ReturnsAsync(new VideoFile { HostFolder = "/media", Filename = "movie.mkv" });

        Mock<IJobDispatcher> dispatcher = new();
        if (!queueAvailable)
            dispatcher
                .Setup(d =>
                    d.DispatchJob<VideoEncodeJob>(
                        libraryId,
                        folderId,
                        "123",
                        "/media/movie.mkv",
                        null
                    )
                )
                .Throws(new InvalidOperationException("Queue is not running"));

        IActionResult result = await BuildController(repository.Object, dispatcher.Object)
            .RetryIncompleteEncode(row.Id);

        if (queueAvailable)
        {
            result.Should().BeOfType<OkObjectResult>();
            repository.Verify(r => r.RemoveAsync(row), Times.Once);
        }
        else
        {
            ObjectResult failure = result.Should().BeOfType<ObjectResult>().Subject;
            failure.StatusCode.Should().Be(StatusCodes.Status409Conflict);
            repository.Verify(r => r.RemoveAsync(row), Times.Never);
        }
    }
}
