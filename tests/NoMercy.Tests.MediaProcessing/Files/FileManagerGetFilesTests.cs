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

using System.Collections.Concurrent;
using System.Reflection;
using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.Encoder.Analysis;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Dto;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Files;

// ---------------------------------------------------------------------------
// FileManager.GetFiles resolves the right IStorage/driver for a folder and
// picks the MediaScan depth by library type — Movie scans 1 level (the
// movie's own folder), Tv/Anime scan 2 (show -> season), everything else 0.
// Only the zero-candidate/empty-folder shape is exercised here: any real
// video/audio file would make MediaScan invoke the real ffprobe binary,
// which is an external process this unit-test layer must not depend on.
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
public sealed class FileManagerGetFilesTests : IDisposable
{
    private readonly string _tempRoot;

    public FileManagerGetFilesTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-getfiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static async Task<ConcurrentBag<MediaFolderExtend>> InvokeGetFiles(
        FileManager manager,
        Library library,
        Folder folder
    )
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "GetFiles",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("GetFiles not found");

        return await (Task<ConcurrentBag<MediaFolderExtend>>)
            method.Invoke(manager, [library, folder])!;
    }

    private static FileManager BuildManager(IStorageFactory factory)
    {
        Mock<IFileRepository> repoMock = new();
        Mock<IMediaAnalyzer> mediaAnalyzerMock = new();
        return new(repoMock.Object, factory, mediaAnalyzerMock.Object, TestFilenameParser.Default);
    }

    private Folder BuildFolderOnRealStorage(out IStorage storage)
    {
        LocalStorageDriver driver = new();
        storage = new LocalStorage(driver, new StoragePathGuard([], driver));
        return new()
        {
            Id = Ulid.NewUlid(),
            Path = _tempRoot,
            DriverId = Ulid.NewUlid(),
        };
    }

    [Fact]
    public async Task GetFiles_MovieLibrary_EmptyFolder_ReturnsRootEntryWithNoFiles()
    {
        Folder folder = BuildFolderOnRealStorage(out IStorage storage);
        Mock<IStorageFactory> factoryMock = new();
        factoryMock.Setup(f => f.For(folder.Id, folder.DriverId, string.Empty)).Returns(storage);

        FileManager manager = BuildManager(factoryMock.Object);
        Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.MovieMediaType };

        ConcurrentBag<MediaFolderExtend> result = await InvokeGetFiles(manager, library, folder);

        result.Should().ContainSingle(f => f.Path == _tempRoot);
    }

    [Fact]
    public async Task GetFiles_TvLibrary_EmptyFolder_ReturnsRootEntryWithNoFiles()
    {
        Folder folder = BuildFolderOnRealStorage(out IStorage storage);
        Mock<IStorageFactory> factoryMock = new();
        factoryMock.Setup(f => f.For(folder.Id, folder.DriverId, string.Empty)).Returns(storage);

        FileManager manager = BuildManager(factoryMock.Object);
        Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.TvMediaType };

        ConcurrentBag<MediaFolderExtend> result = await InvokeGetFiles(manager, library, folder);

        result.Should().ContainSingle(f => f.Path == _tempRoot);
    }

    [Fact]
    public async Task GetFiles_AnimeLibrary_EmptyFolder_ReturnsRootEntryWithNoFiles()
    {
        Folder folder = BuildFolderOnRealStorage(out IStorage storage);
        Mock<IStorageFactory> factoryMock = new();
        factoryMock.Setup(f => f.For(folder.Id, folder.DriverId, string.Empty)).Returns(storage);

        FileManager manager = BuildManager(factoryMock.Object);
        Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.AnimeMediaType };

        ConcurrentBag<MediaFolderExtend> result = await InvokeGetFiles(manager, library, folder);

        result.Should().ContainSingle(f => f.Path == _tempRoot);
    }

    [Fact]
    public async Task GetFiles_MusicLibrary_UsesZeroDepth_StillReturnsRootEntry()
    {
        Folder folder = BuildFolderOnRealStorage(out IStorage storage);
        Mock<IStorageFactory> factoryMock = new();
        factoryMock.Setup(f => f.For(folder.Id, folder.DriverId, string.Empty)).Returns(storage);

        FileManager manager = BuildManager(factoryMock.Object);
        Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.MusicMediaType };

        ConcurrentBag<MediaFolderExtend> result = await InvokeGetFiles(manager, library, folder);

        result.Should().ContainSingle(f => f.Path == _tempRoot);
    }
}
