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
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Inbox;

[Trait("Category", "Unit")]
public class InboxRoutingServiceTests : IDisposable
{
    // -----------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------

    private readonly MediaContext _context;
    private readonly Mock<IStorageFactory> _storageFactoryMock;
    private readonly Mock<IStorage> _storageMock;
    private readonly Mock<IStorageDriver> _driverMock;
    private readonly Mock<JobDispatcher> _dispatcherMock;

    public InboxRoutingServiceTests()
    {
        DbContextOptionsBuilder<MediaContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");

        _context = new(optionsBuilder.Options);
        _context.Database.OpenConnection();
        _context.Database.EnsureCreated();
        _context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");

        _driverMock = new();
        _driverMock.Setup(d => d.GetFullPath(It.IsAny<string>())).Returns<string>(p => p);
        _driverMock.Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>()));
        _driverMock.Setup(d => d.CreateDirectory(It.IsAny<string>()));
        _driverMock.Setup(d => d.DirectoryExists(It.IsAny<string>())).Returns(true);

        _storageMock = new();
        _storageMock.Setup(s => s.Driver).Returns(_driverMock.Object);
        _storageMock.Setup(s => s.GetFullPath(It.IsAny<string>())).Returns<string>(p => p);
        _storageMock
            .Setup(s => s.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _storageMock
            .Setup(s =>
                s.WriteAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>())
            )
            .Returns(Task.CompletedTask);
        _storageMock
            .Setup(s => s.SizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0L);
        _storageMock
            .Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _storageFactoryMock = new();
        _storageFactoryMock
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(_storageMock.Object);

        _dispatcherMock = new();
    }

    public void Dispose()
    {
        _context.Database.CloseConnection();
        _context.Dispose();
    }

    private InboxRoutingService MakeService() =>
        new(_storageFactoryMock.Object, _dispatcherMock.Object);

    // -----------------------------------------------------------------------
    // Seed helpers
    // -----------------------------------------------------------------------

    private (Library library, Folder folder, Ulid profileId) SeedLibraryWithProfile(string type)
    {
        Ulid driverId = Ulid.NewUlid();
        Ulid profileId = Ulid.NewUlid();

        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = $"{type} library",
            Type = type,
        };

        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            Path = $"/media/{type}",
            DriverId = driverId,
        };

        EncodingPresetFolder profileFolder = new()
        {
            PresetId = profileId,
            FolderId = folder.Id,
            IsDefault = true,
        };

        FolderLibrary folderLibrary = new() { FolderId = folder.Id, LibraryId = library.Id };

        _context.Libraries.Add(library);
        _context.Folders.Add(folder);
        _context.EncodingPresetFolders.Add(profileFolder);
        _context.FolderLibrary.Add(folderLibrary);
        _context.SaveChanges();

        return (library, folder, profileId);
    }

    private Folder SeedFolderWithoutProfile(Library library)
    {
        Ulid driverId = Ulid.NewUlid();

        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            Path = "/media/noprofile",
            DriverId = driverId,
        };

        FolderLibrary folderLibrary = new() { FolderId = folder.Id, LibraryId = library.Id };

        _context.Folders.Add(folder);
        _context.FolderLibrary.Add(folderLibrary);
        _context.SaveChanges();

        return folder;
    }

    private static CandidateMatch MakeCandidate(string provider, string externalId, string title) =>
        new()
        {
            Provider = provider,
            ExternalId = externalId,
            Title = title,
            Year = 2020,
            Score = 0.95,
        };

    private static ClassificationResult MakeClassification(
        string detectedType,
        string confidence,
        CandidateMatch[]? candidates = null
    ) =>
        new()
        {
            DetectedType = detectedType,
            Confidence = confidence,
            Candidates = candidates ?? [MakeCandidate("tmdb", "12345", "Test Title")],
        };

    // -----------------------------------------------------------------------
    // ResolveDestinations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResolveDestinations_OneValidMovieDestination_ReturnsOne()
    {
        SeedLibraryWithProfile("movie");
        InboxRoutingService service = MakeService();

        List<InboxDestination> results = await service.ResolveDestinations("movie", _context);

        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task ResolveDestinations_FolderWithoutProfile_IsIncluded()
    {
        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "movie library no profile",
            Type = "movie",
        };
        _context.Libraries.Add(library);
        _context.SaveChanges();

        Folder folder = SeedFolderWithoutProfile(library);

        InboxRoutingService service = MakeService();
        List<InboxDestination> results = await service.ResolveDestinations("movie", _context);

        results.Should().HaveCount(1);
        results[0].LibraryId.Should().Be(library.Id);
        results[0].FolderId.Should().Be(folder.Id);
        results[0].ProfileId.Should().Be(Ulid.Empty);
    }

    [Fact]
    public async Task ResolveDestinations_TypeMismatchedLibrary_IsExcluded()
    {
        SeedLibraryWithProfile("tv");

        InboxRoutingService service = MakeService();
        List<InboxDestination> results = await service.ResolveDestinations("movie", _context);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveDestinations_TwoValidDestinations_ReturnsBoth()
    {
        SeedLibraryWithProfile("movie");
        SeedLibraryWithProfile("movie");

        InboxRoutingService service = MakeService();
        List<InboxDestination> results = await service.ResolveDestinations("movie", _context);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task ResolveDestinations_MapsProfileIdAndDriverId()
    {
        (Library _, Folder folder, Ulid profileId) = SeedLibraryWithProfile("movie");

        InboxRoutingService service = MakeService();
        List<InboxDestination> results = await service.ResolveDestinations("movie", _context);

        InboxDestination dest = results.Single();
        dest.ProfileId.Should().Be(profileId);
        dest.DriverId.Should().Be(folder.DriverId);
        dest.FolderId.Should().Be(folder.Id);
    }

    // -----------------------------------------------------------------------
    // Route — auto branch
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Route_HighConfidence_SingleMatch_SingleDestination_ReturnsAuto()
    {
        SeedLibraryWithProfile("movie");
        InboxRoutingService service = MakeService();

        ClassificationResult classification = MakeClassification("movie", "high");
        RouteOutcome outcome = await service.Route(
            classification,
            "inbox/The Matrix (1999).mkv",
            Ulid.NewUlid(),
            _context
        );

        outcome.Mode.Should().Be("auto");
        outcome.Destination.Should().NotBeNull();
        outcome.Item.Should().NotBeNull();
        outcome.Item.Status.Should().Be("Routing");
    }

    [Fact]
    public async Task Route_HighConfidence_MultipleMatches_RequiresReview()
    {
        SeedLibraryWithProfile("movie");
        ClassificationResult classification = MakeClassification(
            "movie",
            "high",
            [MakeCandidate("tmdb", "1", "Original"), MakeCandidate("tmdb", "2", "Remake")]
        );

        RouteOutcome outcome = await MakeService()
            .Route(classification, "inbox/Original (1984).mkv", Ulid.NewUlid(), _context);

        outcome.Mode.Should().Be("review");
        outcome.Item.Status.Should().Be("NeedsReview");
    }

    [Fact]
    public async Task ExecuteAssignment_CrossDriver_StreamsWithoutReadingWholeFile()
    {
        (Library library, Folder folder, _) = SeedLibraryWithProfile("movie");
        Ulid sourceDriverId = Ulid.NewUlid();
        Mock<IStorage> source = new();
        Mock<IStorage> destinationStorage = new();
        MemoryStream copied = new();
        source
            .Setup(s => s.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Whole-file read attempted"));
        source
            .Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4]));
        source
            .Setup(s => s.SizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        destinationStorage
            .Setup(s => s.OpenWriteAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(copied);
        destinationStorage
            .Setup(s => s.SizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        _storageFactoryMock
            .Setup(f => f.For(Ulid.Empty, sourceDriverId, string.Empty))
            .Returns(source.Object);
        _storageFactoryMock
            .Setup(f => f.For(folder.Id, folder.DriverId, folder.Path))
            .Returns(destinationStorage.Object);

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = "inbox/movie.mkv",
            DriverId = sourceDriverId,
            DetectedType = "movie",
            Status = "NeedsReview",
            Candidates = [],
        };
        InboxDestination destination = new()
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            DriverId = folder.DriverId,
            FolderPath = folder.Path,
        };

        await MakeService()
            .ExecuteAssignment(item, MakeCandidate("tmdb", "123", "Movie"), destination, _context);

        copied.ToArray().Should().Equal(1, 2, 3, 4);
        source.Verify(
            s => s.DeleteAsync(item.SourcePath, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task ExecuteAssignment_InvalidExternalId_LeavesSourceInPlace()
    {
        (Library library, Folder folder, _) = SeedLibraryWithProfile("movie");
        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = "inbox/movie.mkv",
            DriverId = folder.DriverId,
            DetectedType = "movie",
            Status = "NeedsReview",
            Candidates = [],
        };
        InboxDestination destination = new()
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            DriverId = folder.DriverId,
            FolderPath = folder.Path,
        };

        Func<Task> act = () =>
            MakeService()
                .ExecuteAssignment(
                    item,
                    MakeCandidate("tmdb", "abc", "Movie"),
                    destination,
                    _context
                );

        await act.Should().ThrowAsync<FormatException>();
        _driverMock.Verify(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        item.Status.Should().Be("NeedsReview");
    }

    // -----------------------------------------------------------------------
    // Route — review branch: zero destinations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Route_ZeroDestinations_ReturnsReview()
    {
        InboxRoutingService service = MakeService();

        ClassificationResult classification = MakeClassification("movie", "high");
        RouteOutcome outcome = await service.Route(
            classification,
            "inbox/movie.mkv",
            Ulid.NewUlid(),
            _context
        );

        outcome.Mode.Should().Be("review");
        outcome.Destination.Should().BeNull();
        outcome.Item.Status.Should().Be("NeedsReview");
    }

    // -----------------------------------------------------------------------
    // Route — review branch: multiple destinations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Route_MultipleDestinations_ReturnsReview()
    {
        SeedLibraryWithProfile("movie");
        SeedLibraryWithProfile("movie");

        InboxRoutingService service = MakeService();
        ClassificationResult classification = MakeClassification("movie", "high");

        RouteOutcome outcome = await service.Route(
            classification,
            "inbox/movie.mkv",
            Ulid.NewUlid(),
            _context
        );

        outcome.Mode.Should().Be("review");
        outcome.Item.Status.Should().Be("NeedsReview");
    }

    // -----------------------------------------------------------------------
    // Route — review branch: medium/low confidence
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("medium")]
    [InlineData("low")]
    public async Task Route_NonHighConfidence_ReturnsReview(string confidence)
    {
        SeedLibraryWithProfile("movie");
        InboxRoutingService service = MakeService();

        ClassificationResult classification = MakeClassification("movie", confidence);
        RouteOutcome outcome = await service.Route(
            classification,
            "inbox/movie.mkv",
            Ulid.NewUlid(),
            _context
        );

        outcome.Mode.Should().Be("review");
        outcome.Item.Status.Should().Be("NeedsReview");
    }

    // -----------------------------------------------------------------------
    // Route — InboxItem always populated
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Route_ReviewPath_PopulatesInboxItemWithCandidates()
    {
        InboxRoutingService service = MakeService();

        CandidateMatch candidate = MakeCandidate("tmdb", "999", "Unknown Film");
        ClassificationResult classification = MakeClassification("movie", "low", [candidate]);

        RouteOutcome outcome = await service.Route(
            classification,
            "inbox/unknown.mkv",
            Ulid.NewUlid(),
            _context
        );

        outcome.Item.DetectedType.Should().Be("movie");
        outcome.Item.Confidence.Should().Be("low");
        outcome.Item.Candidates.Should().HaveCount(1);
        outcome.Item.Candidates[0].Title.Should().Be("Unknown Film");
        outcome.Item.TargetLibraryId.Should().BeNull();
        outcome.Item.TargetFolderId.Should().BeNull();
        outcome.Item.TargetProfileId.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // ExecuteAuto — movie: MoveAsync + MovieImportJob dispatched
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAuto_Movie_CallsMoveAsync_AndDispatchesMovieImportJob()
    {
        (Library library, Folder folder, _) = SeedLibraryWithProfile("movie");

        InboxRoutingService service = MakeService();

        CandidateMatch candidate = MakeCandidate("tmdb", "603", "The Matrix");
        InboxDestination destination = new()
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            ProfileId = Ulid.NewUlid(),
            DriverId = folder.DriverId,
            FolderPath = folder.Path,
        };

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = "inbox/The Matrix (1999).mkv",
            DriverId = folder.DriverId,
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

        await service.ExecuteAuto(outcome, _context);

        // Same-driver branch: uses Driver.MoveFile with absolute paths, not IStorage.MoveAsync.
        _driverMock.Verify(
            d => d.MoveFile("inbox/The Matrix (1999).mkv", "The Matrix (1999).mkv"),
            Times.Once
        );

        _dispatcherMock.Verify(d => d.DispatchJob<MovieImportJob>(603, library.Id), Times.Once);

        item.Status.Should().Be("Imported");
        item.TargetLibraryId.Should().Be(library.Id);
        item.TargetFolderId.Should().Be(folder.Id);
    }

    // -----------------------------------------------------------------------
    // ExecuteAuto — tv: MoveAsync + ShowImportJob dispatched
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAuto_Tv_DispatchesShowImportJob()
    {
        (Library library, Folder folder, _) = SeedLibraryWithProfile("tv");

        InboxRoutingService service = MakeService();

        CandidateMatch candidate = MakeCandidate("tmdb", "1396", "Breaking Bad");
        InboxDestination destination = new()
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            ProfileId = Ulid.NewUlid(),
            DriverId = folder.DriverId,
            FolderPath = folder.Path,
        };

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = "inbox/Breaking Bad S01E01.mkv",
            DriverId = folder.DriverId,
            DetectedType = "tv",
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

        await service.ExecuteAuto(outcome, _context);

        _dispatcherMock.Verify(d => d.DispatchJob<ShowImportJob>(1396, library.Id), Times.Once);

        item.Status.Should().Be("Imported");
    }

    // -----------------------------------------------------------------------
    // ExecuteAuto — anime: ShowImportJob dispatched
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAuto_Anime_DispatchesShowImportJob()
    {
        (Library library, Folder folder, _) = SeedLibraryWithProfile("anime");

        InboxRoutingService service = MakeService();

        CandidateMatch candidate = MakeCandidate("tmdb", "31478", "Frieren");
        InboxDestination destination = new()
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            ProfileId = Ulid.NewUlid(),
            DriverId = folder.DriverId,
            FolderPath = folder.Path,
        };

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = "inbox/Frieren - 01.mkv",
            DriverId = folder.DriverId,
            DetectedType = "anime",
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

        await service.ExecuteAuto(outcome, _context);

        _dispatcherMock.Verify(d => d.DispatchJob<ShowImportJob>(31478, library.Id), Times.Once);

        item.Status.Should().Be("Imported");
    }

    // -----------------------------------------------------------------------
    // ExecuteAuto — music: AudioImportJob dispatched with libraryId/folderId/path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAuto_Music_DispatchesAudioImportJob()
    {
        (Library library, Folder folder, _) = SeedLibraryWithProfile("music");

        InboxRoutingService service = MakeService();

        CandidateMatch candidate = MakeCandidate("musicbrainz", "some-release-id", "Album Name");
        InboxDestination destination = new()
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            ProfileId = Ulid.NewUlid(),
            DriverId = folder.DriverId,
            FolderPath = "/media/music",
        };

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = "inbox/Artist - Album/01 - Track.flac",
            DriverId = folder.DriverId,
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

        await service.ExecuteAuto(outcome, _context);

        _dispatcherMock.Verify(
            d => d.DispatchJob<AudioImportJob>(library.Id, folder.Id, It.IsAny<string>()),
            Times.Once
        );

        item.Status.Should().Be("Imported");
    }

    // -----------------------------------------------------------------------
    // ExecuteAuto — VideoEncodeJob is NEVER dispatched
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAuto_Movie_DoesNotDispatchVideoEncodeJob()
    {
        (Library library, Folder folder, _) = SeedLibraryWithProfile("movie");

        InboxRoutingService service = MakeService();

        CandidateMatch candidate = MakeCandidate("tmdb", "603", "The Matrix");
        InboxDestination destination = new()
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            ProfileId = Ulid.NewUlid(),
            DriverId = folder.DriverId,
            FolderPath = folder.Path,
        };

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = "inbox/The Matrix (1999).mkv",
            DriverId = folder.DriverId,
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

        await service.ExecuteAuto(outcome, _context);

        _dispatcherMock.Verify(
            d =>
                d.DispatchJob<VideoEncodeJob>(
                    It.IsAny<Ulid>(),
                    It.IsAny<Ulid>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Ulid?>()
                ),
            Times.Never
        );
    }

    // -----------------------------------------------------------------------
    // ExecuteAuto — profile-less destination: TargetProfileId is null, not Ulid.Empty
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAuto_ProfileLessDestination_SetsTargetProfileIdToNull()
    {
        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "movie library no profile",
            Type = "movie",
        };
        _context.Libraries.Add(library);
        _context.SaveChanges();

        Folder folder = SeedFolderWithoutProfile(library);

        InboxRoutingService service = MakeService();

        CandidateMatch candidate = MakeCandidate("tmdb", "603", "The Matrix");
        InboxDestination destination = new()
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            ProfileId = Ulid.Empty,
            DriverId = folder.DriverId,
            FolderPath = folder.Path,
        };

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = "inbox/The Matrix (1999).mkv",
            DriverId = folder.DriverId,
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

        await service.ExecuteAuto(outcome, _context);

        item.TargetProfileId.Should().BeNull();

        InboxItem? persisted = await _context.InboxItems.FindAsync(item.Id);
        persisted.Should().NotBeNull();
        persisted!.TargetProfileId.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // ExecuteAuto — status transitions: Routing -> Imported
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAuto_SetsStatusToImported_AfterMove()
    {
        (Library library, Folder folder, _) = SeedLibraryWithProfile("movie");

        InboxRoutingService service = MakeService();

        CandidateMatch candidate = MakeCandidate("tmdb", "100", "Test Movie");
        InboxDestination destination = new()
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            ProfileId = Ulid.NewUlid(),
            DriverId = folder.DriverId,
            FolderPath = folder.Path,
        };

        InboxItem item = new()
        {
            Id = Ulid.NewUlid(),
            SourcePath = "inbox/Test Movie (2020).mkv",
            DriverId = folder.DriverId,
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

        await service.ExecuteAuto(outcome, _context);

        item.Status.Should().Be("Imported");

        InboxItem? persisted = await _context.InboxItems.FindAsync(item.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be("Imported");
    }
}
