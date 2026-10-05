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
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Storage;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Analysis;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// A rescan that resolves nothing has two causes that look identical in the result and
/// need opposite handling. If the library's own storage dropped — an NFS mount that went
/// away, an S3 bucket that timed out — every title resolves nothing at once and clearing
/// would empty a library that is still fully on disk. If the root reads fine and only
/// this title resolved nothing, the media is gone, and keeping the rows leaves a title in
/// the UI that plays nothing while holding a VideoFile other tables point at.
/// <para>
/// Both tests run the same scan against a real seeded MediaContext, the database
/// <c>Paths</c> opens for itself. The single difference is whether the library ROOT reads
/// back; the title's own folder never does, in either case.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class MissingMediaIsClearedOnlyWhenTheRootReadsBackTests : IDisposable
{
    private const int MovieId = 550;
    private const string RootPath = "Films";

    private readonly List<Ulid> _libraryIds = [];
    private readonly List<Ulid> _folderIds = [];
    private readonly List<Ulid> _driverIds = [];

    public void Dispose()
    {
        using MediaContext cleanup = new();
        cleanup.FolderLibrary.RemoveRange(
            cleanup.FolderLibrary.Where(link => _libraryIds.Contains(link.LibraryId))
        );
        cleanup.Folders.RemoveRange(cleanup.Folders.Where(f => _folderIds.Contains(f.Id)));
        cleanup.Libraries.RemoveRange(cleanup.Libraries.Where(l => _libraryIds.Contains(l.Id)));
        cleanup.SaveChanges();
        cleanup.Drivers.RemoveRange(cleanup.Drivers.Where(d => _driverIds.Contains(d.Id)));
        cleanup.SaveChanges();
    }

    private Library SeedLibraryWithRoot()
    {
        Ulid libraryId = Ulid.NewUlid();
        _libraryIds.Add(libraryId);

        Library library = new()
        {
            Id = libraryId,
            Title = "Films",
            Type = MediaTypes.MovieMediaType,
        };

        Driver driver = new()
        {
            Id = Ulid.NewUlid(),
            Name = $"test-driver-{Ulid.NewUlid()}",
            Type = "local",
        };
        _driverIds.Add(driver.Id);

        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            Path = RootPath,
            DriverId = driver.Id,
        };
        _folderIds.Add(folder.Id);

        using MediaContext context = new();
        context.Drivers.Add(driver);
        context.Libraries.Add(library);
        context.Folders.Add(folder);
        context.FolderLibrary.Add(new(folder.Id, library.Id));
        context.SaveChanges();

        return library;
    }

    /// <summary>
    /// The title's own folder never exists — that is the empty scan under test. Only the
    /// library root's answer differs between the two cases.
    /// </summary>
    private static FileManager BuildManager(Mock<IFileRepository> repoMock, bool rootReadsBack) =>
        BuildManager(repoMock, rootReadsBack, readableRecordedPath: null);

    private static FileManager BuildManager(
        Mock<IFileRepository> repoMock,
        bool rootReadsBack,
        string? readableRecordedPath
    )
    {
        Mock<IStorage> storageMock = new();
        storageMock.Setup(storage => storage.Exists(It.IsAny<string>())).Returns(false);
        storageMock.Setup(storage => storage.Exists(RootPath)).Returns(rootReadsBack);

        if (readableRecordedPath is not null)
            storageMock.Setup(storage => storage.Exists(readableRecordedPath)).Returns(true);
        storageMock
            .Setup(storage => storage.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns(
                (string first, string[] rest) =>
                {
                    string result = first.TrimEnd('/');
                    foreach (string segment in rest)
                        result = $"{result}/{segment.TrimStart('/')}";
                    return result;
                }
            );
        storageMock.Setup(storage => storage.Driver).Returns(Mock.Of<IStorageDriver>());

        Mock<IStorageFactory> factoryMock = new();
        factoryMock
            .Setup(factory => factory.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(storageMock.Object);

        return new(
            repoMock.Object,
            factoryMock.Object,
            Mock.Of<IStorageDriver>(),
            Mock.Of<IMediaAnalyzer>(),
            TestFilenameParser.Default
        );
    }

    private static Mock<IFileRepository> RepositoryResolving(Movie movie)
    {
        Mock<IFileRepository> repoMock = new();
        repoMock
            .Setup(repository => repository.MediaType(It.IsAny<int>(), It.IsAny<Library>()))
            .ReturnsAsync((movie, (Tv?)null, MediaTypes.MovieMediaType));
        repoMock
            .Setup(repository =>
                repository.GetRecordedVideoFileLocationsByMovieIdAsync(It.IsAny<int>())
            )
            .ReturnsAsync([]);
        return repoMock;
    }

    [Fact]
    public async Task RootDoesNotReadBack_KeepsTheRecords()
    {
        Library library = SeedLibraryWithRoot();
        Movie movie = new() { Id = MovieId, Folder = "/Fight Club (1999)" };
        Mock<IFileRepository> repoMock = RepositoryResolving(movie);

        FileManager manager = BuildManager(repoMock, rootReadsBack: false);

        await manager.FindFiles(MovieId, library);

        repoMock.Verify(
            repository => repository.DeleteVideoFilesAndMetadataByMovieIdAsync(It.IsAny<int>()),
            Times.Never,
            "a storage outage is not a deletion — the library is still on disk"
        );
    }

    [Fact]
    public async Task RootReadsBack_AndNothingResolved_ClearsTheRecords()
    {
        Library library = SeedLibraryWithRoot();
        Movie movie = new() { Id = MovieId, Folder = "/Fight Club (1999)" };
        Mock<IFileRepository> repoMock = RepositoryResolving(movie);

        FileManager manager = BuildManager(repoMock, rootReadsBack: true);

        await manager.FindFiles(MovieId, library);

        repoMock.Verify(
            repository => repository.DeleteVideoFilesAndMetadataByMovieIdAsync(MovieId),
            Times.Once,
            "the root reads fine and the media resolved nothing, so it is gone"
        );
    }

    /// <summary>
    /// A library root registered one level above where the media actually lives reads
    /// back fine and resolves every title to nothing — the exact shape that wiped a
    /// movie whose folder was sitting on disk the whole time. The registered path is
    /// the only witness that the media is still there, so it decides the delete.
    /// </summary>
    [Fact]
    public async Task RootReadsBack_ButTheRegisteredMediaIsStillReadable_KeepsTheRecords()
    {
        Library library = SeedLibraryWithRoot();
        Movie movie = new() { Id = MovieId, Folder = "/Fight Club (1999)" };
        Mock<IFileRepository> repoMock = RepositoryResolving(movie);

        const string hostFolder = $"{RootPath}/{RootPath}/Fight Club (1999)";
        const string filename = "/Fight Club (1999).NoMercy.m3u8";

        repoMock
            .Setup(repository =>
                repository.GetRecordedVideoFileLocationsByMovieIdAsync(It.IsAny<int>())
            )
            .ReturnsAsync([new(_folderIds[^1].ToString(), hostFolder, filename)]);

        FileManager manager = BuildManager(
            repoMock,
            rootReadsBack: true,
            readableRecordedPath: $"{hostFolder}{filename}"
        );

        await manager.FindFiles(MovieId, library);

        repoMock.Verify(
            repository => repository.DeleteVideoFilesAndMetadataByMovieIdAsync(It.IsAny<int>()),
            Times.Never,
            "the media the rows point at is still on disk — the scan failed, not the media"
        );
    }
}
