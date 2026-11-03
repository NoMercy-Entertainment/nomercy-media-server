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
using NoMercy.Api.Controllers.V1.Dashboard.Admin;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using NoMercy.Monitoring;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Controllers;

public class StorageControllerTests
{
    [Fact]
    public async Task Storage_ReturnsUsageForLibraryFolder()
    {
        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            DriverId = Ulid.NewUlid(),
            Path = "/media/example",
        };
        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Type = "movie",
            Title = "Movies",
        };
        folder.FolderLibraries.Add(
            new(folder.Id, library.Id) { Folder = folder, Library = library }
        );
        Mock<IFolderRepository> folders = new();
        folders.Setup(repository => repository.GetAllFoldersAsync(default)).ReturnsAsync([folder]);
        Mock<IStorage> storage = new();
        storage
            .Setup(source => source.List(string.Empty, null, true))
            .Returns([new("example.mp4", false, 16384, DateTimeOffset.UtcNow)]);
        Mock<IStorageFactory> factory = new();
        factory
            .Setup(source => source.For(folder.Id, folder.DriverId, folder.Path))
            .Returns(storage.Object);
        StorageMonitor.Storage = [];
        ServerController controller = new(
            logger: null!,
            resourceMonitor: null!,
            updateChecker: null!,
            appLifetime: null!,
            serverConfiguration: null!,
            fileRepository: null!,
            fileListService: null!,
            jobDispatcher: null!,
            queueRunner: null!,
            eventBus: null!,
            wallpaperService: null!,
            networkDiscovery: null!,
            httpClientFactory: null!,
            storageDriver: null!,
            storageFactory: factory.Object,
            libraryRepository: null!,
            folderRepository: folders.Object,
            imageRepository: null!,
            authTokenStore: null!,
            audioFingerprinter: null!,
            activityLogger: null!,
            filenameParser: null!
        );

        object action = controller.Storage();
        IActionResult result = action is Task<IActionResult> pending
            ? await pending
            : (IActionResult)action;

        OkObjectResult response = Assert.IsType<OkObjectResult>(result);
        StorageDto entry = Assert.Single(Assert.IsType<List<StorageDto>>(response.Value));
        entry.Path.Should().Be(folder.Path);
        entry.Data.Used.Should().Be(2);
        entry.Data.Movies.Should().Be(100);
    }
}
