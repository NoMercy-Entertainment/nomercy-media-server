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
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Analysis;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Dto;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Files;

// ---------------------------------------------------------------------------
// StoreVideoItem used to derive a row's Folder by searching the file's path for
// the title's own folder name, and fell back to the whole storage path when it
// was not there. A title's stored folder is often spelled differently from the
// directory on disk ("Ocean's.Thirteen" against "Oceans.Thirteen"), so those
// rows were written with a storage path for a Folder. The URL a client composes
// from that resolves for nobody, and the boot sweep then deletes the row even
// though the file is still on disk. Folder must come from the library root, as
// FileLogic's scan already does.
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
public sealed class FileManagerStoreVideoItemFolderTests : IDisposable
{
    private readonly string _libraryRoot;

    public FileManagerStoreVideoItemFolderTests()
    {
        _libraryRoot = Path.Combine(
            Path.GetTempPath(),
            $"nm-storevideofolder-{Guid.NewGuid():N}",
            "Films"
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

    private static async Task InvokeStoreVideoItem(FileManager manager, MediaFile item)
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "StoreVideoItem",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("StoreVideoItem not found");

        await (Task)method.Invoke(manager, [item])!;
    }

    // A scan narrows each library root to the title's own directory and keeps the
    // root's id, so Folders holds that title directory while LibraryRootFolders
    // holds the root the stored Folder is measured from.
    private (FileManager Manager, Func<VideoFile?> Stored) BuildManager(string titleDirectory)
    {
        VideoFile? stored = null;

        Mock<IFileRepository> repoMock = new();
        repoMock
            .Setup(repo => repo.StoreMetadata(It.IsAny<Metadata>()))
            .ReturnsAsync(Ulid.NewUlid());
        repoMock
            .Setup(repo => repo.StoreVideoFile(It.IsAny<VideoFile>()))
            .Callback<VideoFile>(videoFile => stored = videoFile)
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

        Ulid rootId = Ulid.NewUlid();
        Ulid driverId = Ulid.NewUlid();
        SetPrivateProperty(
            manager,
            "LibraryRootFolders",
            new List<Folder>
            {
                new()
                {
                    Id = rootId,
                    Path = _libraryRoot,
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
                    Id = rootId,
                    Path = titleDirectory,
                    DriverId = driverId,
                },
            }
        );

        return (manager, () => stored);
    }

    [Fact]
    public async Task StoreVideoItem_TitleFolderSpelledDifferentlyOnDisk_StoresTheLibraryRelativeFolder()
    {
        string onDisk = Path.Combine(_libraryRoot, "Oceans.Thirteen.(2007)");
        Directory.CreateDirectory(onDisk);
        MediaFile item = new()
        {
            Path = Path.Combine(onDisk, "Oceans.Thirteen.(2007).NoMercy.mp4"),
        };

        (FileManager manager, Func<VideoFile?> stored) = BuildManager(onDisk);
        SetPrivateProperty(
            manager,
            "Movie",
            new Movie
            {
                Id = 298,
                Title = "Ocean's Thirteen",
                Folder = "/Ocean's.Thirteen.(2007)",
            }
        );

        await InvokeStoreVideoItem(manager, item);

        stored().Should().NotBeNull();
        stored()!.Folder.Should().Be("/Oceans.Thirteen.(2007)");
        stored()!.Filename.Should().Be("/Oceans.Thirteen.(2007).NoMercy.mp4");
    }

    [Fact]
    public async Task StoreVideoItem_EpisodeInASeasonFolder_KeepsTheFullPathBelowTheLibraryRoot()
    {
        string titleDirectory = Path.Combine(_libraryRoot, "Haikyu!!.(2014)");
        string onDisk = Path.Combine(titleDirectory, "Haikyu.S01E01");
        Directory.CreateDirectory(onDisk);
        MediaFile item = new() { Path = Path.Combine(onDisk, "Haikyu.S01E01.NoMercy.m3u8") };

        (FileManager manager, Func<VideoFile?> stored) = BuildManager(titleDirectory);
        SetPrivateProperty(
            manager,
            "Show",
            new Tv
            {
                Id = 60863,
                Title = "Haikyu!!",
                Folder = "/Haikyuu!!.(2014)",
            }
        );

        await InvokeStoreVideoItem(manager, item);

        stored().Should().NotBeNull();
        stored()!.Folder.Should().Be("/Haikyu!!.(2014)/Haikyu.S01E01");
    }
}
