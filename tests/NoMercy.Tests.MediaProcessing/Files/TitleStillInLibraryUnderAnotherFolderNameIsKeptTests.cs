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
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;
using Serilog.Events;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// A rescan that resolves nothing for a title may only remove its rows when the media
/// is really gone. Real folders on disk; only the repository is a mock.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TitleStillInLibraryUnderAnotherFolderNameIsKeptTests : IDisposable
{
    private const int FirstMovieId = 9_000_001;
    private const int TitleCount = 10;

    private readonly string _tempRoot;
    private readonly List<Ulid> _libraryIds = [];
    private readonly List<Ulid> _folderIds = [];
    private readonly List<Ulid> _driverIds = [];
    private readonly List<LogEntry> _log = [];

    public TitleStillInLibraryUnderAnotherFolderNameIsKeptTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-keep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Logger.LogEmitted += Capture;
    }

    public void Dispose()
    {
        Logger.LogEmitted -= Capture;

        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);

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

    private void Capture(LogEntry entry)
    {
        lock (_log)
            _log.Add(entry);
    }

    private List<string> Logged(LogEventLevel level, string containing)
    {
        lock (_log)
            return
            [
                .. _log.Where(entry => entry.LogLevel == level)
                    .Select(entry => (string)entry.Message)
                    .Where(message => message.Contains(containing, StringComparison.Ordinal)),
            ];
    }

    private static string FolderNameOf(int index) => $"Made.Up.Title.{index:00}.({2000 + index})";

    private static Movie MovieOf(int index) =>
        new()
        {
            Id = FirstMovieId + index,
            Title = $"Made Up Title {index:00}",
            ReleaseDate = new DateTime(2000 + index, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Folder = $"/{FolderNameOf(index)}",
        };

    /// <summary>A movie library of ten titles, each with its folder and one media file on disk.</summary>
    private (Library Library, Folder Root) SeedLibraryOnDisk()
    {
        Ulid libraryId = Ulid.NewUlid();
        _libraryIds.Add(libraryId);
        Library library = new()
        {
            Id = libraryId,
            Title = "Made Up Films",
            Type = MediaTypes.MovieMediaType,
        };

        Driver driver = new()
        {
            Id = Ulid.NewUlid(),
            Name = $"test-driver-{Ulid.NewUlid()}",
            Type = "local",
        };
        _driverIds.Add(driver.Id);

        Folder root = new()
        {
            Id = Ulid.NewUlid(),
            Path = _tempRoot,
            DriverId = driver.Id,
        };
        _folderIds.Add(root.Id);

        using MediaContext context = new();
        context.Drivers.Add(driver);
        context.Libraries.Add(library);
        context.Folders.Add(root);
        context.FolderLibrary.Add(new(root.Id, library.Id));
        context.SaveChanges();

        for (int index = 0; index < TitleCount; index++)
        {
            string titleFolder = Path.Combine(_tempRoot, FolderNameOf(index));
            Directory.CreateDirectory(titleFolder);
            File.WriteAllText(Path.Combine(titleFolder, $"{FolderNameOf(index)}.m3u8"), "#EXTM3U");
        }

        return (library, root);
    }

    private string TitleFolderOnDisk(int index) => Path.Combine(_tempRoot, FolderNameOf(index));

    private RecordedVideoFileLocation RecordedLocationOf(Folder root, int index) =>
        new(
            root.Id.ToString(),
            $"{_tempRoot.Replace('\\', '/')}/{FolderNameOf(index)}",
            $"/{FolderNameOf(index)}.m3u8"
        );

    private Mock<IFileRepository> RepositoryOf(Folder root)
    {
        Mock<IFileRepository> repoMock = new();
        repoMock
            .Setup(repository => repository.MediaType(It.IsAny<int>(), It.IsAny<Library>()))
            .ReturnsAsync(
                (int id, Library library) =>
                    (MovieOf(id - FirstMovieId), (Tv?)null, MediaTypes.MovieMediaType)
            );
        repoMock
            .Setup(repository =>
                repository.GetRecordedVideoFileLocationsByMovieIdAsync(It.IsAny<int>())
            )
            .ReturnsAsync((int id) => [RecordedLocationOf(root, id - FirstMovieId)]);
        repoMock
            .Setup(repository =>
                repository.GetRecordedVideoFileLocationsByLibraryAsync(
                    It.IsAny<Ulid>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(
                Enumerable
                    .Range(0, TitleCount)
                    .ToDictionary(
                        index => FirstMovieId + index,
                        index => new List<RecordedVideoFileLocation>
                        {
                            RecordedLocationOf(root, index),
                        }
                    )
            );
        return repoMock;
    }

    /// <summary>A manager per title, as <c>LibraryManager.RescanFiles</c> builds one per rescan.</summary>
    private static FileManager BuildManager(Mock<IFileRepository> repoMock)
    {
        LocalStorageDriver driver = new();
        IStorage storage = new LocalStorage(driver, new StoragePathGuard([], driver));
        Mock<IStorageFactory> factoryMock = new();
        factoryMock
            .Setup(factory => factory.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(storage);

        return new(
            repoMock.Object,
            factoryMock.Object,
            Mock.Of<IMediaAnalyzer>(),
            TestFilenameParser.Default
        );
    }

    [Fact]
    public async Task StoredFolderRenamedWithASuffix_KeepsTheRecords()
    {
        (Library library, Folder root) = SeedLibraryOnDisk();
        string renamed = $"{TitleFolderOnDisk(0)}.back";
        Directory.Move(TitleFolderOnDisk(0), renamed);
        Mock<IFileRepository> repoMock = RepositoryOf(root);

        await BuildManager(repoMock).FindFiles(FirstMovieId, library);

        repoMock.Verify(
            repository => repository.DeleteVideoFilesAndMetadataByMovieIdAsync(It.IsAny<int>()),
            Times.Never,
            "the media sits in the same root in a folder named <stored name>.back"
        );
        Logged(LogEventLevel.Warning, "sibling copy")
            .Should()
            .ContainSingle(message =>
                message.Contains("Made Up Title 00") && message.Contains(renamed)
            );
    }

    [Fact]
    public async Task ThreeOfTenTitlesMissing_KeepsEveryRecord_AndLogsOneError()
    {
        (Library library, Folder root) = SeedLibraryOnDisk();
        for (int index = 0; index < 3; index++)
            Directory.Delete(TitleFolderOnDisk(index), recursive: true);
        Mock<IFileRepository> repoMock = RepositoryOf(root);

        for (int index = 0; index < 3; index++)
            await BuildManager(repoMock).FindFiles(FirstMovieId + index, library);

        repoMock.Verify(
            repository => repository.DeleteVideoFilesAndMetadataByMovieIdAsync(It.IsAny<int>()),
            Times.Never,
            "30% of the library going missing at once is a storage problem, not three deletions"
        );
        Logged(LogEventLevel.Error, library.Id.ToString())
            .Should()
            .ContainSingle(message => message.Contains("3 of 10"));
    }

    [Fact]
    public async Task OneOfTenTitlesMissingWithNoCopyAnywhere_ClearsThatTitle()
    {
        (Library library, Folder root) = SeedLibraryOnDisk();
        Directory.Delete(TitleFolderOnDisk(0), recursive: true);
        Mock<IFileRepository> repoMock = RepositoryOf(root);

        await BuildManager(repoMock).FindFiles(FirstMovieId, library);

        repoMock.Verify(
            repository => repository.DeleteVideoFilesAndMetadataByMovieIdAsync(FirstMovieId),
            Times.Once,
            "a title really removed from disk is still cleared"
        );
        repoMock.Verify(
            repository => repository.DeleteVideoFilesAndMetadataByMovieIdAsync(It.IsAny<int>()),
            Times.Once
        );
    }
}
