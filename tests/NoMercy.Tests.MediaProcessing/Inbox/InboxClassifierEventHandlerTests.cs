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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.Events.Inbox;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Factory;

namespace NoMercy.Tests.MediaProcessing.Inbox;

[Trait("Category", "Unit")]
public class InboxClassifierEventHandlerTests : IDisposable
{
    // -----------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------

    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;
    private readonly Mock<IEventBus> _eventBusMock;
    private readonly Mock<IInboxMetadataProbe> _probeMock;
    private readonly Mock<IInboxAudioTagReader> _tagReaderMock;
    private readonly Mock<IMediaTypeClassifier> _mediaTypeClassifierMock;
    private readonly Mock<IStorageFactory> _storageFactoryMock;

    public InboxClassifierEventHandlerTests()
    {
        string dbName = Guid.NewGuid().ToString();
        _connection = new($"DataSource={dbName};Mode=Memory;Cache=Shared");
        _connection.Open();

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new(options);
        _context.Database.EnsureCreated();
        _context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");

        _eventBusMock = new();
        _eventBusMock
            .Setup(bus =>
                bus.Subscribe<FileCreatedEvent>(
                    It.IsAny<Func<FileCreatedEvent, CancellationToken, Task>>()
                )
            )
            .Returns(Mock.Of<IDisposable>());
        _eventBusMock
            .Setup(bus =>
                bus.PublishAsync(It.IsAny<InboxItemDetectedEvent>(), It.IsAny<CancellationToken>())
            )
            .Returns(Task.CompletedTask);

        _probeMock = new();
        _probeMock
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        _probeMock
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);
        _probeMock
            .Setup(p => p.LookupMusicReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CandidateMatch?)null);

        _tagReaderMock = new();
        _tagReaderMock
            .Setup(r =>
                r.ReadAsync(It.IsAny<string>(), It.IsAny<Ulid>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync((InboxAudioTags?)null);

        _mediaTypeClassifierMock = new();
        _mediaTypeClassifierMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<int?>()))
            .ReturnsAsync("tv");

        _storageFactoryMock = new();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private MediaContext CreateSharedContext()
    {
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .Options;
        return new(options);
    }

    private InboxClassifierEventHandler MakeHandler()
    {
        InboxClassifier classifier = new(
            _probeMock.Object,
            _tagReaderMock.Object,
            _mediaTypeClassifierMock.Object
        );
        InboxRoutingService routing = new(_storageFactoryMock.Object, new());

        return new(
            NullLogger<InboxClassifierEventHandler>.Instance,
            _eventBusMock.Object,
            classifier,
            routing,
            CreateSharedContext,
            _storageFactoryMock.Object
        );
    }

    private Ulid SeedInboxLibraryWithFolder(string folderPath)
    {
        Ulid libraryId = Ulid.NewUlid();
        Ulid folderId = Ulid.NewUlid();
        Ulid driverId = Ulid.NewUlid();

        _context.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Inbox",
                Type = MediaTypes.InboxMediaType,
            }
        );

        _context.Folders.Add(
            new()
            {
                Id = folderId,
                Path = folderPath,
                DriverId = driverId,
            }
        );

        _context.FolderLibrary.Add(new() { LibraryId = libraryId, FolderId = folderId });

        _context.SaveChanges();

        return libraryId;
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task NonInboxLibraryType_IsIgnored_ClassifyNeverCalled()
    {
        InboxClassifierEventHandler handler = MakeHandler();

        FileCreatedEvent @event = new()
        {
            FolderPath = "/media/movies/Inception (2010)/Inception.mkv",
            LibraryId = Ulid.NewUlid(),
            LibraryType = MediaTypes.MovieMediaType,
        };

        await handler.OnFileCreated(@event, CancellationToken.None);

        _probeMock.Verify(
            p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        _probeMock.Verify(
            p =>
                p.SearchTvAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task InboxEvent_LibraryNotFound_ReturnsWithoutClassify()
    {
        InboxClassifierEventHandler handler = MakeHandler();

        FileCreatedEvent @event = new()
        {
            FolderPath = "/inbox/somefile.mkv",
            LibraryId = Ulid.NewUlid(),
            LibraryType = MediaTypes.InboxMediaType,
        };

        await handler.OnFileCreated(@event, CancellationToken.None);

        _probeMock.Verify(
            p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task InboxEvent_WithSeededLibrary_TriggersClassifyAndRoute()
    {
        Ulid libraryId = SeedInboxLibraryWithFolder("/inbox");

        // Storage mock returns one .mkv child
        Mock<IStorage> storageMock = new();
        storageMock
            .Setup(s => s.List("", null, false))
            .Returns([new("somefile.mkv", false, 1024, DateTimeOffset.UtcNow)]);
        storageMock
            .Setup(s => s.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns<string, string[]>(
                (parent, segments) => $"{parent}/{string.Join('/', segments)}"
            );
        _storageFactoryMock
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(storageMock.Object);

        List<InboxItemDetectedEvent> published = [];
        _eventBusMock
            .Setup(bus =>
                bus.PublishAsync(It.IsAny<InboxItemDetectedEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback<InboxItemDetectedEvent, CancellationToken>((evt, _) => published.Add(evt))
            .Returns(Task.CompletedTask);

        InboxClassifierEventHandler handler = MakeHandler();

        FileCreatedEvent @event = new()
        {
            FolderPath = "/inbox/somefile.mkv",
            LibraryId = libraryId,
            LibraryType = MediaTypes.InboxMediaType,
        };

        await handler.OnFileCreated(@event, CancellationToken.None);

        // .mkv is a video extension → probe is called for movie/tv
        _probeMock.Verify(
            p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.AtLeastOnce
        );

        published.Should().ContainSingle();
        published[0].Status.Should().Be("NeedsReview");
        published[0].DetectedType.Should().NotBeNullOrEmpty();
    }

    // -----------------------------------------------------------------------
    // Integration test: loose dropped file classified by real extension
    // -----------------------------------------------------------------------

    [Trait("Category", "Integration")]
    [Fact]
    public async Task LooseDroppedFile_IsClassifiedByExtension_NotInboxDir()
    {
        // Arrange: real temp inbox dir with a real .mkv file
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "nm-inbox-handler-it-" + Path.GetRandomFileName()
        );
        string inboxDir = Path.Combine(tempRoot, "inbox");
        Directory.CreateDirectory(inboxDir);

        string droppedFilePath = Path.Combine(inboxDir, "The Matrix (1999).mkv");
        await File.WriteAllBytesAsync(droppedFilePath, [0x1A, 0x45, 0xDF, 0xA3]);

        try
        {
            // Real storage factory scoped to the inbox dir
            LocalStorageDriver realDriver = new();
            Mock<IDriverConfigResolver> resolverMock = new();
            resolverMock
                .Setup(r => r.Resolve(It.IsAny<Ulid>()))
                .Returns(("local", "{\"rootPath\":\"\"}"));

            StorageFactory realStorageFactory = new(
                realDriver,
                NullLogger<StorageFactory>.Instance,
                resolverMock.Object
            );

            // Seed inbox library pointing at the real temp inbox dir
            string dbName = Guid.NewGuid().ToString();
            SqliteConnection connection = new($"DataSource={dbName};Mode=Memory;Cache=Shared");
            connection.Open();

            DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
                .UseSqlite(connection)
                .Options;

            await using MediaContext seedContext = new(options);
            seedContext.Database.EnsureCreated();
            seedContext.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");

            Ulid libraryId = Ulid.NewUlid();
            Ulid folderId = Ulid.NewUlid();
            Ulid driverId = Ulid.NewUlid();

            seedContext.Libraries.Add(
                new()
                {
                    Id = libraryId,
                    Title = "Inbox",
                    Type = MediaTypes.InboxMediaType,
                }
            );
            seedContext.Folders.Add(
                new()
                {
                    Id = folderId,
                    Path = inboxDir,
                    DriverId = driverId,
                }
            );
            seedContext.FolderLibrary.Add(new() { LibraryId = libraryId, FolderId = folderId });
            await seedContext.SaveChangesAsync();

            Mock<IEventBus> eventBusMock = new();
            eventBusMock
                .Setup(bus =>
                    bus.Subscribe<FileCreatedEvent>(
                        It.IsAny<Func<FileCreatedEvent, CancellationToken, Task>>()
                    )
                )
                .Returns(Mock.Of<IDisposable>());

            List<InboxItemDetectedEvent> published = [];
            eventBusMock
                .Setup(bus =>
                    bus.PublishAsync(
                        It.IsAny<InboxItemDetectedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Callback<InboxItemDetectedEvent, CancellationToken>((evt, _) => published.Add(evt))
                .Returns(Task.CompletedTask);

            Mock<IInboxMetadataProbe> probeMock = new();
            probeMock
                .Setup(p =>
                    p.SearchMoviesAsync(
                        It.IsAny<string>(),
                        It.IsAny<int?>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync([]);
            probeMock
                .Setup(p =>
                    p.SearchTvAsync(
                        It.IsAny<string>(),
                        It.IsAny<int?>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync([]);
            probeMock
                .Setup(p =>
                    p.LookupMusicReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())
                )
                .ReturnsAsync((CandidateMatch?)null);

            Mock<IInboxAudioTagReader> tagReaderMock = new();
            tagReaderMock
                .Setup(r =>
                    r.ReadAsync(It.IsAny<string>(), It.IsAny<Ulid>(), It.IsAny<CancellationToken>())
                )
                .ReturnsAsync((InboxAudioTags?)null);

            Mock<IMediaTypeClassifier> mediaTypeClassifierMock = new();
            mediaTypeClassifierMock
                .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<int?>()))
                .ReturnsAsync("tv");

            InboxClassifier classifier = new(
                probeMock.Object,
                tagReaderMock.Object,
                mediaTypeClassifierMock.Object
            );
            InboxRoutingService routing = new(realStorageFactory, new());

            MediaContext ContextFactory()
            {
                DbContextOptions<MediaContext> ctx = new DbContextOptionsBuilder<MediaContext>()
                    .UseSqlite(connection)
                    .Options;
                return new(ctx);
            }

            InboxClassifierEventHandler handler = new(
                NullLogger<InboxClassifierEventHandler>.Instance,
                eventBusMock.Object,
                classifier,
                routing,
                ContextFactory,
                realStorageFactory
            );

            FileCreatedEvent @event = new()
            {
                FolderPath = inboxDir,
                LibraryId = libraryId,
                LibraryType = MediaTypes.InboxMediaType,
            };

            // Act
            await handler.OnFileCreated(@event, CancellationToken.None);

            // Assert: one InboxItemDetectedEvent published
            published.Should().ContainSingle("one item dropped, one event expected");

            // Assert: detected type is NOT "unknown" — real extension was seen
            published[0].DetectedType.Should().NotBe("unknown");

            // Assert: an InboxItem was saved with SourcePath = the FILE path (not the dir)
            await using MediaContext verifyContext = ContextFactory();
            List<InboxItem> items = await verifyContext.InboxItems.ToListAsync();
            items.Should().ContainSingle();
            string normalizedSource = Path.GetFullPath(
                items[0].SourcePath.Replace('/', Path.DirectorySeparatorChar)
            );
            normalizedSource
                .Should()
                .Be(droppedFilePath, "SourcePath must be the FILE path, not the inbox directory");
            normalizedSource
                .Should()
                .NotBe(inboxDir, "SourcePath must not be the inbox root directory");

            connection.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    // -----------------------------------------------------------------------
    // Dedup: second fire does NOT create a duplicate InboxItem
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SecondFireForSameChild_DoesNotCreateDuplicateInboxItem()
    {
        Ulid libraryId = SeedInboxLibraryWithFolder("/inbox");

        Mock<IStorage> storageMock = new();
        storageMock
            .Setup(s => s.List("", null, false))
            .Returns([new("The Matrix (1999).mkv", false, 1024, DateTimeOffset.UtcNow)]);
        storageMock
            .Setup(s => s.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns<string, string[]>(
                (parent, segments) => $"{parent}/{string.Join('/', segments)}"
            );
        _storageFactoryMock
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(storageMock.Object);

        List<InboxItemDetectedEvent> published = [];
        _eventBusMock
            .Setup(bus =>
                bus.PublishAsync(It.IsAny<InboxItemDetectedEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback<InboxItemDetectedEvent, CancellationToken>((evt, _) => published.Add(evt))
            .Returns(Task.CompletedTask);

        InboxClassifierEventHandler handler = MakeHandler();

        FileCreatedEvent @event = new()
        {
            FolderPath = "/inbox/The Matrix (1999).mkv",
            LibraryId = libraryId,
            LibraryType = MediaTypes.InboxMediaType,
        };

        // First fire
        await handler.OnFileCreated(@event, CancellationToken.None);

        // Second fire (watcher debounce re-fire)
        await handler.OnFileCreated(@event, CancellationToken.None);

        // Only one InboxItem in DB
        List<InboxItem> items = await _context.InboxItems.ToListAsync();
        items.Should().ContainSingle("second fire must be deduped");

        // Only one event published
        published.Should().ContainSingle("event published only once");
    }
}
