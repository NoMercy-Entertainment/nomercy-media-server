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
    [Theory]
    [InlineData("movie", "example.mp4", 66, 0, 0)]
    [InlineData("tv", "example.mkv", 0, 66, 0)]
    [InlineData("music", "example.flac", 0, 0, 66)]
    public async Task Storage_ReturnsUsageForLibraryFolder(
        string libraryType,
        string mediaFile,
        long movies,
        long shows,
        long music
    )
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
            Type = libraryType,
            Title = "Library",
        };
        folder.FolderLibraries.Add(
            new(folder.Id, library.Id) { Folder = folder, Library = library }
        );
        Mock<IFolderRepository> folders = new();
        folders
            .Setup(repository => repository.GetStorageFoldersAsync(default))
            .ReturnsAsync([folder]);
        Mock<IStorage> storage = new();
        storage
            .Setup(source => source.List(string.Empty, null, true))
            .Returns([
                new(mediaFile, false, 16384, DateTimeOffset.UtcNow),
                new("notes.txt", false, 8192, DateTimeOffset.UtcNow),
            ]);
        Mock<IStorageFactory> factory = new();
        factory
            .Setup(source => source.For(folder.Id, folder.DriverId, folder.Path))
            .Returns(storage.Object);
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

        IActionResult result = await controller.Storage();

        OkObjectResult response = Assert.IsType<OkObjectResult>(result);
        StorageDto entry = Assert.Single(Assert.IsType<List<StorageDto>>(response.Value));
        entry.Path.Should().Be(folder.Path);
        entry.Data.Used.Should().Be(3);
        entry.Data.Movies.Should().Be(movies);
        entry.Data.Shows.Should().Be(shows);
        entry.Data.Music.Should().Be(music);
        entry.Data.Other.Should().Be(33);
    }
}
