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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Factory;

namespace NoMercy.Tests.MediaProcessing.Inbox;

/// <summary>
/// End-to-end integration tests for inbox routing that use the REAL local storage
/// driver rooted at temp directories. These tests prove actual file movement happens
/// and that the same-driver path-scoping in InboxRoutingService is correct.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InboxRoutingIntegrationTests : IDisposable
{
    // Temp root that owns both inbox and library dirs; cleaned up in Dispose.
    private readonly string _tempRoot;
    private readonly string _inboxDir;
    private readonly string _movieLibDir;
    private readonly string _musicLibDir;

    private readonly Ulid _sharedDriverId;
    private readonly StorageFactory _storageFactory;
    private readonly Mock<JobDispatcher> _dispatcherMock;

    // -----------------------------------------------------------------------
    // Setup / teardown
    // -----------------------------------------------------------------------

    public InboxRoutingIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "nm-inbox-it-" + Path.GetRandomFileName());
        _inboxDir = Path.Combine(_tempRoot, "inbox");
        _movieLibDir = Path.Combine(_tempRoot, "movies");
        _musicLibDir = Path.Combine(_tempRoot, "music");

        Directory.CreateDirectory(_inboxDir);
        Directory.CreateDirectory(_movieLibDir);
        Directory.CreateDirectory(_musicLibDir);

        // Single shared driver ID for same-driver move tests.
        _sharedDriverId = Ulid.NewUlid();

        // Build a real StorageFactory backed by a real LocalStorageDriver.
        // The driver config resolver maps any driver ID to ("local", {rootPath:""})
        // so that the folder subPath becomes the allowed root — matching how the
        // production DB-backed resolver behaves for local-disk folders.
        LocalStorageDriver realDriver = new();
        Mock<IDriverConfigResolver> resolverMock = new();
        resolverMock
            .Setup(r => r.Resolve(It.IsAny<Ulid>()))
            .Returns(("local", "{\"rootPath\":\"\"}"));

        _storageFactory = new(realDriver, NullLogger<StorageFactory>.Instance, resolverMock.Object);

        _dispatcherMock = new();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private MediaContext BuildContext()
    {
        DbContextOptionsBuilder<MediaContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");
        MediaContext context = new(optionsBuilder.Options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return context;
    }

    /// <summary>
    /// Seeds: Library + Folder (pointing at libDir) + EncodingPresetFolder + FolderLibrary.
    /// Returns the seeded destination ready for use in a RouteOutcome.
    /// </summary>
    private InboxDestination SeedLibraryDestination(
        MediaContext context,
        string libType,
        string libDir
    )
    {
        Ulid libraryId = Ulid.NewUlid();
        Ulid folderId = Ulid.NewUlid();
        Ulid profileId = Ulid.NewUlid();

        Library library = new()
        {
            Id = libraryId,
            Title = $"{libType} lib",
            Type = libType,
        };

        Folder folder = new()
        {
            Id = folderId,
            Path = libDir,
            DriverId = _sharedDriverId,
        };

        EncodingPresetFolder profileFolder = new()
        {
            PresetId = profileId,
            FolderId = folderId,
            IsDefault = true,
        };

        FolderLibrary folderLibrary = new() { FolderId = folderId, LibraryId = libraryId };

        context.Libraries.Add(library);
        context.Folders.Add(folder);
        context.EncodingPresetFolders.Add(profileFolder);
        context.FolderLibrary.Add(folderLibrary);
        context.SaveChanges();

        return new()
        {
            LibraryId = libraryId,
            FolderId = folderId,
            ProfileId = profileId,
            DriverId = _sharedDriverId,
            FolderPath = libDir,
        };
    }

    private InboxRoutingService BuildService() => new(_storageFactory, _dispatcherMock.Object);

    // -----------------------------------------------------------------------
    // Test 1: Auto-route a movie — file must land in the movie library folder
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAuto_Movie_MovesFileToDestinationAndDispatchesImport()
    {
        // Arrange: write a real .mkv into the inbox temp dir.
        string inboxFilePath = Path.Combine(_inboxDir, "The Matrix (1999).mkv");
        await File.WriteAllBytesAsync(inboxFilePath, [0x00, 0x01, 0x02, 0x03]);

        await using MediaContext context = BuildContext();
        InboxDestination destination = SeedLibraryDestination(context, "movie", _movieLibDir);

        CandidateMatch candidate = new()
        {
            Provider = "tmdb",
            ExternalId = "603",
            Title = "The Matrix",
            Year = 1999,
            Score = 0.99,
        };

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = inboxFilePath,
            DriverId = _sharedDriverId,
            DetectedType = "movie",
            Confidence = "high",
            Status = "Routing",
            Candidates = [candidate],
        };

        RouteOutcome outcome = new()
        {
            Mode = "auto",
            Destination = destination,
            Item = item,
        };

        InboxRoutingService service = BuildService();

        // Act
        await service.ExecuteAuto(outcome, context);

        // Assert — file physically moved to the movie library directory.
        string expectedDest = Path.Combine(_movieLibDir, "The Matrix (1999).mkv");
        File.Exists(expectedDest).Should().BeTrue("file must exist in destination folder");
        File.Exists(inboxFilePath).Should().BeFalse("file must no longer be in inbox");

        // Assert — InboxItem updated.
        item.Status.Should().Be("Imported");
        item.TargetLibraryId.Should().Be(destination.LibraryId);
        item.TargetFolderId.Should().Be(destination.FolderId);

        // Assert — MovieImportJob dispatched with tmdbId=603 and correct libraryId.
        _dispatcherMock.Verify(
            d => d.DispatchJob<MovieImportJob>(603, destination.LibraryId),
            Times.Once
        );
    }

    // -----------------------------------------------------------------------
    // Test 2: User assignment — same shape via ExecuteAssignment
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAssignment_UserChosenMatch_MovesAndDispatchesShowImport()
    {
        // Arrange: a TV show episode dropped in inbox.
        string inboxFilePath = Path.Combine(_inboxDir, "Breaking Bad S01E01.mkv");
        await File.WriteAllBytesAsync(inboxFilePath, [0xAB, 0xCD]);

        // Use the movie lib dir re-purposed as tv lib dir for isolation.
        string tvLibDir = Path.Combine(_tempRoot, "tv");
        Directory.CreateDirectory(tvLibDir);

        await using MediaContext context = BuildContext();
        InboxDestination destination = SeedLibraryDestination(context, "tv", tvLibDir);

        CandidateMatch userChoice = new()
        {
            Provider = "tmdb",
            ExternalId = "1396",
            Title = "Breaking Bad",
            Year = 2008,
            Score = 0.98,
        };

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = inboxFilePath,
            DriverId = _sharedDriverId,
            DetectedType = "tv",
            Confidence = "medium",
            Status = "NeedsReview",
            Candidates = [userChoice],
        };

        InboxRoutingService service = BuildService();

        // Act — user-driven assignment (bypasses the Route auto/review decision).
        await service.ExecuteAssignment(item, userChoice, destination, context);

        // Assert — file physically in tv library directory.
        string expectedDest = Path.Combine(tvLibDir, "Breaking Bad S01E01.mkv");
        File.Exists(expectedDest).Should().BeTrue("file must exist in destination folder");
        File.Exists(inboxFilePath).Should().BeFalse("file must no longer be in inbox");

        item.Status.Should().Be("Imported");

        // ShowImportJob dispatched with tmdbId=1396.
        _dispatcherMock.Verify(
            d => d.DispatchJob<ShowImportJob>(1396, destination.LibraryId),
            Times.Once
        );
    }

    [Fact]
    public async Task ExecuteAssignment_DispatchFailure_RestoresSourceFile()
    {
        string sourcePath = Path.Combine(_inboxDir, "Example (1984).mkv");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        await using MediaContext context = BuildContext();
        InboxDestination destination = SeedLibraryDestination(context, "movie", _movieLibDir);
        CandidateMatch match = new()
        {
            Provider = "tmdb",
            ExternalId = "123",
            Title = "Example",
            Year = 1984,
            Score = 0.95,
        };
        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = sourcePath,
            DriverId = _sharedDriverId,
            DetectedType = "movie",
            Status = "NeedsReview",
            Candidates = [match],
        };
        _dispatcherMock
            .Setup(d => d.DispatchJob<MovieImportJob>(123, destination.LibraryId))
            .Throws(new InvalidOperationException("Queue unavailable"));

        Func<Task> act = () => BuildService().ExecuteAssignment(item, match, destination, context);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(sourcePath).Should().BeTrue();
        File.Exists(Path.Combine(_movieLibDir, "Example (1984).mkv")).Should().BeFalse();
        item.Status.Should().Be("NeedsReview");
    }

    // -----------------------------------------------------------------------
    // Test 3: Auto-route music — file moved + AudioImportJob dispatched
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAuto_Music_MovesFileAndDispatchesAudioImport()
    {
        // Arrange: a real .flac in the inbox dir.
        string inboxFilePath = Path.Combine(_inboxDir, "01 - Track One.flac");
        await File.WriteAllBytesAsync(inboxFilePath, [0xFF, 0xFB, 0x90, 0x00]);

        await using MediaContext context = BuildContext();
        InboxDestination destination = SeedLibraryDestination(context, "music", _musicLibDir);

        CandidateMatch candidate = new()
        {
            Provider = "musicbrainz",
            ExternalId = "some-release-id",
            Title = "Some Album",
            Year = 2022,
            Score = 0.91,
        };

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = inboxFilePath,
            DriverId = _sharedDriverId,
            DetectedType = "music",
            Confidence = "high",
            Status = "Routing",
            Candidates = [candidate],
        };

        RouteOutcome outcome = new()
        {
            Mode = "auto",
            Destination = destination,
            Item = item,
        };

        InboxRoutingService service = BuildService();

        // Act
        await service.ExecuteAuto(outcome, context);

        // Assert — file physically in music library directory.
        string expectedDest = Path.Combine(_musicLibDir, "01 - Track One.flac");
        File.Exists(expectedDest).Should().BeTrue("file must exist in destination folder");
        File.Exists(inboxFilePath).Should().BeFalse("file must no longer be in inbox");

        item.Status.Should().Be("Imported");

        // AudioImportJob dispatched with (libraryId, folderId, movedPath).
        _dispatcherMock.Verify(
            d =>
                d.DispatchJob<AudioImportJob>(
                    destination.LibraryId,
                    destination.FolderId,
                    It.IsAny<string>()
                ),
            Times.Once
        );
    }
}
