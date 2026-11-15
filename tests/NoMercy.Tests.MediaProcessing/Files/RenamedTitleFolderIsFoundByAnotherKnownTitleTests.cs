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
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// A title whose stored folder name is not on disk is looked up under the other names
/// it is known by, and the stored folder follows the folder found.
/// Real folders on disk; only the repository is a mock.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RenamedTitleFolderIsFoundByAnotherKnownTitleTests : IDisposable
{
    private const int TitleId = 9_100_001;
    private const string StoredFolder = "/Old.Made.Up.Title.(2001)";
    private const string OtherKnownTitle = "New Made Up Title";
    private const string FolderOnDisk = "New.Made.Up.Title.(2001)";

    private static readonly DateTime Released = new(2001, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _tempRoot;
    private readonly List<Ulid> _libraryIds = [];
    private readonly List<Ulid> _folderIds = [];
    private readonly List<Ulid> _driverIds = [];

    public RenamedTitleFolderIsFoundByAnotherKnownTitleTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-renamed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
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

    /// <summary>A library with one root, holding one title folder with one media file.</summary>
    private Library SeedLibraryOnDisk(string libraryType, string titleFolderOnDisk)
    {
        Ulid libraryId = Ulid.NewUlid();
        _libraryIds.Add(libraryId);
        Library library = new()
        {
            Id = libraryId,
            Title = "Made Up Library",
            Type = libraryType,
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

        string titleFolder = Path.Combine(_tempRoot, titleFolderOnDisk);
        Directory.CreateDirectory(titleFolder);
        File.WriteAllText(Path.Combine(titleFolder, $"{titleFolderOnDisk}.m3u8"), "#EXTM3U");

        return library;
    }

    private static Mock<IFileRepository> RepositoryOf(Movie? movie, Tv? show, string type)
    {
        Mock<IFileRepository> repoMock = new();
        repoMock
            .Setup(repository => repository.MediaType(It.IsAny<int>(), It.IsAny<Library>()))
            .ReturnsAsync((movie, show, type));
        repoMock
            .Setup(repository =>
                repository.GetRecordedVideoFileLocationsByMovieIdAsync(It.IsAny<int>())
            )
            .ReturnsAsync([]);
        repoMock
            .Setup(repository =>
                repository.GetRecordedVideoFileLocationsByLibraryAsync(
                    It.IsAny<Ulid>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync([]);
        repoMock
            .Setup(repository => repository.GetOtherKnownTitlesAsync(TitleId, It.IsAny<bool>()))
            .ReturnsAsync([OtherKnownTitle]);
        return repoMock;
    }

    private static Movie StoredMovie() =>
        new()
        {
            Id = TitleId,
            Title = "Old Made Up Title",
            ReleaseDate = Released,
            Folder = StoredFolder,
        };

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
    public async Task Movie_folder_named_after_another_known_title_is_found_and_stored()
    {
        Library library = SeedLibraryOnDisk(MediaTypes.MovieMediaType, FolderOnDisk);
        Movie movie = StoredMovie();
        Mock<IFileRepository> repoMock = RepositoryOf(movie, null, MediaTypes.MovieMediaType);

        await BuildManager(repoMock).FindFiles(TitleId, library);

        repoMock.Verify(
            repository => repository.UpdateTitleFolderAsync(TitleId, true, $"/{FolderOnDisk}"),
            Times.Once,
            "the stored folder has to follow the folder the movie really sits in"
        );
        movie.Folder.Should().Be($"/{FolderOnDisk}");
        repoMock.Verify(
            repository => repository.DeleteVideoFilesAndMetadataByMovieIdAsync(It.IsAny<int>()),
            Times.Never,
            "the movie is on disk, so its video file link must stay"
        );
    }

    [Fact]
    public async Task Show_folder_named_after_another_known_title_is_found_and_stored()
    {
        Library library = SeedLibraryOnDisk(MediaTypes.TvMediaType, FolderOnDisk);
        Tv show = new()
        {
            Id = TitleId,
            Title = "Old Made Up Title",
            FirstAirDate = Released,
            Folder = StoredFolder,
        };
        Mock<IFileRepository> repoMock = RepositoryOf(null, show, MediaTypes.TvMediaType);

        await BuildManager(repoMock).FindFiles(TitleId, library);

        repoMock.Verify(
            repository => repository.UpdateTitleFolderAsync(TitleId, false, $"/{FolderOnDisk}"),
            Times.Once
        );
        show.Folder.Should().Be($"/{FolderOnDisk}");
    }

    [Fact]
    public async Task Folder_another_title_already_owns_is_not_taken()
    {
        Library library = SeedLibraryOnDisk(MediaTypes.MovieMediaType, FolderOnDisk);
        Movie movie = StoredMovie();
        Mock<IFileRepository> repoMock = RepositoryOf(movie, null, MediaTypes.MovieMediaType);
        repoMock
            .Setup(repository =>
                repository.IsFolderOfAnotherTitleAsync(TitleId, true, It.IsAny<string>())
            )
            .ReturnsAsync(true);

        await BuildManager(repoMock).FindFiles(TitleId, library);

        repoMock.Verify(
            repository =>
                repository.UpdateTitleFolderAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<string>()
                ),
            Times.Never,
            "the folder is the stored folder of another movie"
        );
        movie.Folder.Should().Be(StoredFolder);
    }

    [Fact]
    public async Task Folder_of_the_same_title_from_another_year_is_not_taken()
    {
        Library library = SeedLibraryOnDisk(MediaTypes.MovieMediaType, "New.Made.Up.Title.(1999)");
        Movie movie = StoredMovie();
        Mock<IFileRepository> repoMock = RepositoryOf(movie, null, MediaTypes.MovieMediaType);

        await BuildManager(repoMock).FindFiles(TitleId, library);

        repoMock.Verify(
            repository =>
                repository.UpdateTitleFolderAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<string>()
                ),
            Times.Never,
            "a remake from another year is another movie"
        );
        movie.Folder.Should().Be(StoredFolder);
    }
}
