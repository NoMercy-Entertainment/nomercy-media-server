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

using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage;

namespace NoMercy.Tests.OpticalMedia.Rip;

/// <summary>
/// <see cref="DiscConfirmationService"/> replaces OpticalMediaController.ConfirmDisc's own
/// inline <c>new LibraryRepository(...)</c> + raw <see cref="System.IO.File"/> calls. These
/// tests cover the folder/library validation branches and that a successful confirm moves the
/// file through the storage facade and notifies the folder-watcher via the event bus.
/// </summary>
[Trait("Category", "Unit")]
public class DiscConfirmationServiceTests
{
    private static CustomMetadata MakeMetadata() =>
        new(Title: "Test Movie", Year: 2024, Type: MediaType.Movie, PosterUrl: null);

    [Fact]
    public async Task ConfirmAsync_UnknownFolder_ReturnsFolderNotFound()
    {
        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository
            .Setup(r => r.GetLibraryFolder(It.IsAny<Ulid>()))
            .ReturnsAsync((Folder?)null);

        DiscConfirmationService service = new(
            libraryRepository.Object,
            Mock.Of<IStorageFactory>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IEventBus>()
        );

        DiscConfirmationResult result = await service.ConfirmAsync(
            Ulid.NewUlid(),
            Ulid.NewUlid(),
            "/tmp/rip.mkv",
            MakeMetadata(),
            CancellationToken.None
        );

        result.Outcome.Should().Be(DiscConfirmationOutcome.FolderNotFound);
        result.Destination.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmAsync_UnknownLibrary_ReturnsLibraryNotFound()
    {
        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository
            .Setup(r => r.GetLibraryFolder(It.IsAny<Ulid>()))
            .ReturnsAsync(new Folder { Id = Ulid.NewUlid(), Path = "/media" });
        libraryRepository
            .Setup(r => r.GetLibraryByIdWithFolders(It.IsAny<Ulid>()))
            .ReturnsAsync((Library?)null);

        DiscConfirmationService service = new(
            libraryRepository.Object,
            Mock.Of<IStorageFactory>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IEventBus>()
        );

        DiscConfirmationResult result = await service.ConfirmAsync(
            Ulid.NewUlid(),
            Ulid.NewUlid(),
            "/tmp/rip.mkv",
            MakeMetadata(),
            CancellationToken.None
        );

        result.Outcome.Should().Be(DiscConfirmationOutcome.LibraryNotFound);
    }

    [Fact]
    public async Task ConfirmAsync_ValidTargets_MovesFileAndPublishesFileCreatedEvent()
    {
        Ulid folderId = Ulid.NewUlid();
        Ulid libraryId = Ulid.NewUlid();
        Folder folder = new()
        {
            Id = folderId,
            Path = "/media/movies",
            DriverId = Ulid.NewUlid(),
        };
        Library library = new()
        {
            Id = libraryId,
            Title = "Movies",
            Type = "movie",
        };

        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository.Setup(r => r.GetLibraryFolder(folderId)).ReturnsAsync(folder);
        libraryRepository.Setup(r => r.GetLibraryByIdWithFolders(libraryId)).ReturnsAsync(library);

        Mock<IStorage> folderStorage = new();
        folderStorage
            .Setup(s => s.OpenWriteAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream());
        folderStorage.Setup(s => s.GetFullPath(It.IsAny<string>())).Returns("/media/movies");

        Mock<IStorageFactory> storageFactory = new();
        storageFactory
            .Setup(f => f.For(folderId, folder.DriverId, string.Empty))
            .Returns(folderStorage.Object);

        Mock<IStorageDriver> storageDriver = new();
        storageDriver.Setup(d => d.OpenRead("/tmp/rip.mkv")).Returns(new MemoryStream());

        Mock<IEventBus> eventBus = new();

        DiscConfirmationService service = new(
            libraryRepository.Object,
            storageFactory.Object,
            storageDriver.Object,
            eventBus.Object
        );

        DiscConfirmationResult result = await service.ConfirmAsync(
            folderId,
            libraryId,
            "/tmp/rip.mkv",
            MakeMetadata(),
            CancellationToken.None
        );

        result.Outcome.Should().Be(DiscConfirmationOutcome.Confirmed);
        result.Destination.Should().Be("Test Movie (2024)/Test Movie (2024).mkv");

        storageDriver.Verify(d => d.DeleteFile("/tmp/rip.mkv"), Times.Once);
        eventBus.Verify(
            b =>
                b.PublishAsync(
                    It.Is<FileCreatedEvent>(e =>
                        e.LibraryId == libraryId && e.LibraryType == "movie"
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task ConfirmAsync_DeleteOfRipOutputThrows_StillReturnsConfirmed()
    {
        // Best-effort cleanup: a locked/already-removed rip file must not fail the confirm —
        // the file has already been copied into the library by this point.
        Ulid folderId = Ulid.NewUlid();
        Ulid libraryId = Ulid.NewUlid();
        Folder folder = new()
        {
            Id = folderId,
            Path = "/media/movies",
            DriverId = Ulid.NewUlid(),
        };
        Library library = new()
        {
            Id = libraryId,
            Title = "Movies",
            Type = "movie",
        };

        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository.Setup(r => r.GetLibraryFolder(folderId)).ReturnsAsync(folder);
        libraryRepository.Setup(r => r.GetLibraryByIdWithFolders(libraryId)).ReturnsAsync(library);

        Mock<IStorage> folderStorage = new();
        folderStorage
            .Setup(s => s.OpenWriteAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream());
        folderStorage.Setup(s => s.GetFullPath(It.IsAny<string>())).Returns("/media/movies");

        Mock<IStorageFactory> storageFactory = new();
        storageFactory
            .Setup(f => f.For(folderId, folder.DriverId, string.Empty))
            .Returns(folderStorage.Object);

        Mock<IStorageDriver> storageDriver = new();
        storageDriver.Setup(d => d.OpenRead("/tmp/rip.mkv")).Returns(new MemoryStream());
        storageDriver.Setup(d => d.DeleteFile("/tmp/rip.mkv")).Throws<IOException>();

        DiscConfirmationService service = new(
            libraryRepository.Object,
            storageFactory.Object,
            storageDriver.Object,
            Mock.Of<IEventBus>()
        );

        DiscConfirmationResult result = await service.ConfirmAsync(
            folderId,
            libraryId,
            "/tmp/rip.mkv",
            MakeMetadata(),
            CancellationToken.None
        );

        result.Outcome.Should().Be(DiscConfirmationOutcome.Confirmed);
    }
}
