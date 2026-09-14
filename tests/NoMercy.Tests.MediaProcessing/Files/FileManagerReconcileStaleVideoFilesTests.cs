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

using System.Reflection;
using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Analysis;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Dto;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Files;

// ---------------------------------------------------------------------------
// FindFiles used to delete every VideoFiles row for a show unconditionally,
// before storage even ran, whenever ANY folder produced a parseable
// candidate. A folder whose items all failed to resolve (a skip inside
// StoreVideoItem) looked identical to "nothing needs deleting there" — its
// pre-existing, still-valid rows were wiped anyway. This proves the narrower
// guard: a folder with a skipped item never enters the eligible-shares set,
// so the repository call that runs afterward cannot touch its rows.
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
public sealed class FileManagerReconcileStaleVideoFilesTests : IDisposable
{
    private readonly string _libraryRoot;

    public FileManagerReconcileStaleVideoFilesTests()
    {
        _libraryRoot = Path.Combine(
            Path.GetTempPath(),
            $"nm-reconcile-stale-{Guid.NewGuid():N}",
            "Shows"
        );
        Directory.CreateDirectory(_libraryRoot);
    }

    public void Dispose()
    {
        string parent = Path.GetDirectoryName(_libraryRoot)!;
        if (Directory.Exists(parent))
            Directory.Delete(parent, recursive: true);
    }

    private static void SetPrivateProperty(FileManager manager, string name, object? value)
    {
        PropertyInfo property =
            typeof(FileManager).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{name} not found");
        property.SetValue(manager, value);
    }

    private static async Task<bool> InvokeStoreVideoItem(FileManager manager, MediaFile item)
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "StoreVideoItem",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("StoreVideoItem not found");

        return await (Task<bool>)method.Invoke(manager, [item])!;
    }

    private static async Task InvokeReconcile(FileManager manager, Library library)
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "ReconcileStaleVideoFilesAsync",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("ReconcileStaleVideoFilesAsync not found");

        await (Task)method.Invoke(manager, [library])!;
    }

    [Fact]
    public async Task OneFolderSkipsEveryItem_ItsShareIsExcludedFromTheEligibleSetSoNothingOfItsIsDeleted()
    {
        string goodTitleDirectory = Path.Combine(_libraryRoot, "Show.Good.(2020)");
        Directory.CreateDirectory(goodTitleDirectory);
        MediaFile goodItem = new()
        {
            Path = Path.Combine(goodTitleDirectory, "Show.Good.S01E01.NoMercy.mkv"),
        };

        // Never resolves under its own root — reaches the "does not resolve
        // under library folder" skip inside StoreVideoItem without needing
        // any real file on disk (nothing after that point touches storage).
        MediaFile badItem = new() { Path = "Unrelated/Show.Bad/Show.Bad.S01E01.NoMercy.mkv" };

        Mock<IFileRepository> repoMock = new();
        repoMock
            .Setup(repo => repo.StoreMetadata(It.IsAny<Metadata>()))
            .ReturnsAsync(Ulid.NewUlid());
        repoMock
            .Setup(repo => repo.StoreVideoFile(It.IsAny<VideoFile>()))
            .Returns(Task.CompletedTask);
        repoMock
            .Setup(repo =>
                repo.DeleteStaleVideoFilesAndMetadataByTvIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<HashSet<RecordedVideoFileLocation>>()
                )
            )
            .Returns(Task.CompletedTask);

        LocalStorageDriver driver = new();
        Mock<IStorageFactory> factoryMock = new();
        factoryMock
            .Setup(factory => factory.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(new LocalStorage(driver, new StoragePathGuard([], driver)));

        FileManager manager = new(
            repoMock.Object,
            factoryMock.Object,
            new Mock<IStorageDriver>().Object,
            new Mock<IMediaAnalyzer>().Object,
            TestFilenameParser.Default
        );

        Ulid rootGood = Ulid.NewUlid();
        Ulid rootBad = Ulid.NewUlid();
        Ulid driverId = Ulid.NewUlid();

        SetPrivateProperty(manager, "Show", new Tv { Id = 42, Title = "Show" });
        SetPrivateProperty(
            manager,
            "LibraryRootFolders",
            new List<Folder>
            {
                new()
                {
                    Id = rootGood,
                    Path = _libraryRoot,
                    DriverId = driverId,
                },
                // Deliberately mismatched so TryGetLibraryRelativeFolder can
                // never resolve badItem's hostFolder under it.
                new()
                {
                    Id = rootBad,
                    Path = "Somewhere/Else/Entirely",
                    DriverId = driverId,
                },
            }
        );
        SetPrivateProperty(
            manager,
            "Folders",
            new List<Folder>
            {
                new()
                {
                    Id = rootGood,
                    Path = goodTitleDirectory,
                    DriverId = driverId,
                },
                new()
                {
                    Id = rootBad,
                    Path = "Unrelated/Show.Bad",
                    DriverId = driverId,
                },
            }
        );

        bool goodStored = await InvokeStoreVideoItem(manager, goodItem);
        bool badStored = await InvokeStoreVideoItem(manager, badItem);

        goodStored.Should().BeTrue();
        badStored.Should().BeFalse();

        Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.TvMediaType };
        await InvokeReconcile(manager, library);

        repoMock.Verify(
            repo =>
                repo.DeleteStaleVideoFilesAndMetadataByTvIdAsync(
                    42,
                    It.Is<List<string>>(shares =>
                        shares.Contains(rootGood.ToString()) && !shares.Contains(rootBad.ToString())
                    ),
                    It.IsAny<HashSet<RecordedVideoFileLocation>>()
                ),
            Times.Once
        );
    }
}
