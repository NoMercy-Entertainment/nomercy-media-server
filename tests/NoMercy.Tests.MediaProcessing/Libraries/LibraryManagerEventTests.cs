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
using NoMercy.Encoder.Analysis;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Factory;
using NoMercy.Tests.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing.Libraries;

public class LibraryManagerEventTests : IDisposable
{
    private static readonly IMediaAnalyzer MediaAnalyzer = new Mock<IMediaAnalyzer>().Object;

    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;

    public LibraryManagerEventTests()
    {
        string dbName = Guid.NewGuid().ToString();
        _connection = new($"DataSource={dbName};Mode=Memory;Cache=Shared");
        _connection.Open();
        _connection.CreateFunction(
            "normalize_search",
            (string? input) => input?.NormalizeSearch() ?? string.Empty
        );

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(
                _connection,
                o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
            )
            .AddInterceptors(new SqliteNormalizeSearchInterceptor())
            .Options;

        _context = new(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ProcessLibrary_NonExistentLibrary_DoesNotPublishEvents()
    {
        InMemoryEventBus bus = new();
        List<IEvent> received = [];

        bus.Subscribe<LibraryScanStartedEvent>(
            (e, _) =>
            {
                received.Add(e);
                return Task.CompletedTask;
            }
        );
        bus.Subscribe<LibraryScanCompletedEvent>(
            (e, _) =>
            {
                received.Add(e);
                return Task.CompletedTask;
            }
        );

        IStorageDriver driver = new LocalStorageDriver();
        StorageFactory storageFactory = new(driver, NullLogger<StorageFactory>.Instance);
        LibraryRepository repo = new(_context, driver);
        JobDispatcher dispatcher = new();
        LibraryManager manager = new(
            repo,
            dispatcher,
            _context,
            driver,
            storageFactory,
            MediaAnalyzer,
            TestFilenameParser.Default,
            NullLogger<LibraryManager>.Instance,
            bus
        );

        await manager.ProcessLibrary(Ulid.NewUlid());

        Assert.Empty(received);
    }

    [Fact]
    public async Task ProcessLibrary_EmptyLibrary_PublishesStartAndCompletedEvents()
    {
        InMemoryEventBus bus = new();
        List<IEvent> received = [];

        bus.Subscribe<LibraryScanStartedEvent>(
            (e, _) =>
            {
                received.Add(e);
                return Task.CompletedTask;
            }
        );
        bus.Subscribe<LibraryScanCompletedEvent>(
            (e, _) =>
            {
                received.Add(e);
                return Task.CompletedTask;
            }
        );

        Ulid libraryId = Ulid.NewUlid();
        _context.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Test Movies",
                Type = "movie",
            }
        );
        await _context.SaveChangesAsync();

        IStorageDriver driver = new LocalStorageDriver();
        StorageFactory storageFactory = new(driver, NullLogger<StorageFactory>.Instance);
        LibraryRepository repo = new(_context, driver);
        JobDispatcher dispatcher = new();
        LibraryManager manager = new(
            repo,
            dispatcher,
            _context,
            driver,
            storageFactory,
            MediaAnalyzer,
            TestFilenameParser.Default,
            NullLogger<LibraryManager>.Instance,
            bus
        );

        await manager.ProcessLibrary(libraryId);

        Assert.Equal(2, received.Count);

        LibraryScanStartedEvent started = Assert.IsType<LibraryScanStartedEvent>(received[0]);
        Assert.Equal(libraryId, started.LibraryId);
        Assert.Equal("Test Movies", started.LibraryName);

        LibraryScanCompletedEvent completed = Assert.IsType<LibraryScanCompletedEvent>(received[1]);
        Assert.Equal(libraryId, completed.LibraryId);
        Assert.Equal("Test Movies", completed.LibraryName);
        Assert.Equal(0, completed.ItemsFound);
        Assert.True(completed.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ProcessLibrary_EmptyLibrary_PublishesZeroQueuedBetweenStartAndCompleted()
    {
        InMemoryEventBus bus = new();
        List<IEvent> received = [];
        bus.Subscribe<LibraryScanStartedEvent>(
            (e, _) =>
            {
                received.Add(e);
                return Task.CompletedTask;
            }
        );
        bus.Subscribe<LibraryImportsQueuedEvent>(
            (e, _) =>
            {
                received.Add(e);
                return Task.CompletedTask;
            }
        );
        bus.Subscribe<LibraryScanCompletedEvent>(
            (e, _) =>
            {
                received.Add(e);
                return Task.CompletedTask;
            }
        );

        Ulid libraryId = Ulid.NewUlid();
        _context.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Test Movies",
                Type = "movie",
            }
        );
        await _context.SaveChangesAsync();

        IStorageDriver driver = new LocalStorageDriver();
        StorageFactory storageFactory = new(driver, NullLogger<StorageFactory>.Instance);
        LibraryManager manager = new(
            new LibraryRepository(_context, driver),
            new JobDispatcher(),
            _context,
            driver,
            storageFactory,
            MediaAnalyzer,
            TestFilenameParser.Default,
            NullLogger<LibraryManager>.Instance,
            bus
        );

        await manager.ProcessLibrary(libraryId);

        Assert.Equal(3, received.Count);
        Assert.IsType<LibraryScanStartedEvent>(received[0]);
        LibraryImportsQueuedEvent queued = Assert.IsType<LibraryImportsQueuedEvent>(received[1]);
        Assert.Equal(libraryId, queued.LibraryId);
        Assert.Equal("Test Movies", queued.LibraryName);
        Assert.Equal(0, queued.Count);
        Assert.IsType<LibraryScanCompletedEvent>(received[2]);
    }

    [Fact]
    public async Task ProcessLibrary_WithoutEventBus_DoesNotThrow()
    {
        Ulid libraryId = Ulid.NewUlid();
        _context.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "No Events Library",
                Type = "movie",
            }
        );
        await _context.SaveChangesAsync();

        IStorageDriver driver = new LocalStorageDriver();
        StorageFactory storageFactory = new(driver, NullLogger<StorageFactory>.Instance);
        LibraryRepository repo = new(_context, driver);
        JobDispatcher dispatcher = new();
        LibraryManager manager = new(
            repo,
            dispatcher,
            _context,
            driver,
            storageFactory,
            MediaAnalyzer,
            TestFilenameParser.Default,
            NullLogger<LibraryManager>.Instance
        );

        await manager.ProcessLibrary(libraryId);
    }

    [Fact]
    public async Task ProcessLibrary_CompletedEvent_HasValidDuration()
    {
        InMemoryEventBus bus = new();
        LibraryScanCompletedEvent? completedEvent = null;

        bus.Subscribe<LibraryScanCompletedEvent>(
            (e, _) =>
            {
                completedEvent = e;
                return Task.CompletedTask;
            }
        );

        Ulid libraryId = Ulid.NewUlid();
        _context.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Duration Test",
                Type = "tv",
            }
        );
        await _context.SaveChangesAsync();

        IStorageDriver driver = new LocalStorageDriver();
        StorageFactory storageFactory = new(driver, NullLogger<StorageFactory>.Instance);
        LibraryRepository repo = new(_context, driver);
        JobDispatcher dispatcher = new();
        LibraryManager manager = new(
            repo,
            dispatcher,
            _context,
            driver,
            storageFactory,
            MediaAnalyzer,
            TestFilenameParser.Default,
            NullLogger<LibraryManager>.Instance,
            bus
        );

        await manager.ProcessLibrary(libraryId);

        Assert.NotNull(completedEvent);
        Assert.True(completedEvent.Duration >= TimeSpan.Zero);
        Assert.True(completedEvent.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ProcessLibrary_StartedEvent_HasCorrectEventMetadata()
    {
        InMemoryEventBus bus = new();
        LibraryScanStartedEvent? startedEvent = null;

        bus.Subscribe<LibraryScanStartedEvent>(
            (e, _) =>
            {
                startedEvent = e;
                return Task.CompletedTask;
            }
        );

        Ulid libraryId = Ulid.NewUlid();
        _context.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Metadata Test",
                Type = "movie",
            }
        );
        await _context.SaveChangesAsync();

        IStorageDriver driver = new LocalStorageDriver();
        StorageFactory storageFactory = new(driver, NullLogger<StorageFactory>.Instance);
        LibraryRepository repo = new(_context, driver);
        JobDispatcher dispatcher = new();
        LibraryManager manager = new(
            repo,
            dispatcher,
            _context,
            driver,
            storageFactory,
            MediaAnalyzer,
            TestFilenameParser.Default,
            NullLogger<LibraryManager>.Instance,
            bus
        );

        await manager.ProcessLibrary(libraryId);

        Assert.NotNull(startedEvent);
        Assert.NotEqual(Guid.Empty, startedEvent.EventId);
        Assert.True(startedEvent.Timestamp <= DateTime.UtcNow);
        Assert.Equal("LibraryScanner", startedEvent.Source);
    }
}
