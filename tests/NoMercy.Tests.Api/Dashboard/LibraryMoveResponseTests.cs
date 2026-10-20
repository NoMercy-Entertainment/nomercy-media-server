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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Media;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.Middleware;
using NoMercy.Data.Activity;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Shows;
using Xunit;
using IDefaultEncodingPresetLinker = NoMercy.MediaProcessing.Libraries.IDefaultEncodingPresetLinker;
using IJobDispatcher = NoMercy.MediaProcessing.Jobs.IJobDispatcher;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// The move endpoint used to answer 200 "Successfully moved item" whenever the
/// file manager returned without throwing, including the cases where it had
/// refused the move or left the files and the database disagreeing.
/// </summary>
[Trait("Category", "Unit")]
public class LibraryMoveResponseTests
{
    private const int ItemId = 42;

    private static readonly Folder TargetFolder = new()
    {
        Id = Ulid.NewUlid(),
        Path = "/library/movies",
        DriverId = Ulid.NewUlid(),
    };

    private static LibrariesController BuildController(MoveToLibraryFolderResult result)
    {
        Mock<IFolderRepository> folderRepository = new();
        folderRepository
            .Setup(repository => repository.GetFolderByIdAsync(TargetFolder.Id))
            .ReturnsAsync(TargetFolder);

        Mock<IFileManager> fileManager = new();
        fileManager
            .Setup(manager => manager.MoveToLibraryFolder(ItemId, TargetFolder))
            .ReturnsAsync(result);

        return new(
            Mock.Of<ILibraryRepository>(),
            Mock.Of<IEncodingPresetRepository>(),
            folderRepository.Object,
            Mock.Of<IJobDispatcher>(),
            Mock.Of<ILanguageRepository>(),
            Mock.Of<IDbContextFactory<MediaContext>>(),
            Mock.Of<IActivityLogger>(),
            Mock.Of<IDefaultEncodingPresetLinker>(),
            fileManager.Object,
            Mock.Of<IAnimeClassificationAuditService>(),
            Mock.Of<ILogger<LibrariesController>>(),
            Mock.Of<IEventBus>(),
            new ServedFolderRegistry()
        );
    }

    private static MoveRequest Request() => new() { Id = ItemId, FolderId = TargetFolder.Id };

    [Theory]
    [InlineData(MoveToLibraryFolderResult.TargetFolderHasNoLibrary, 422)]
    [InlineData(MoveToLibraryFolderResult.SourceFolderNotFound, 404)]
    [InlineData(MoveToLibraryFolderResult.DatabaseUpdateFailed, 500)]
    public async Task Move_RefusedOrFailed_AnswersWithThatFailureStatus(
        MoveToLibraryFolderResult result,
        int expectedStatus
    )
    {
        LibrariesController controller = BuildController(result);

        IActionResult response = await controller.Move(Request());

        response
            .Should()
            .BeOfType<ObjectResult>()
            .Which.StatusCode.Should()
            .Be(expectedStatus, "a move that did not complete must not read as a success");
    }

    [Fact]
    public async Task Move_Completed_KeepsTheOkStatusResponse()
    {
        LibrariesController controller = BuildController(MoveToLibraryFolderResult.Moved);

        IActionResult response = await controller.Move(Request());

        StatusResponseDto<string> body = response
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeOfType<StatusResponseDto<string>>()
            .Subject;

        body.Status.Should().Be("ok");
        body.Args.Should().ContainSingle();
        ((string)body.Args![0]).Should().Be(ItemId.ToString());
    }
}
