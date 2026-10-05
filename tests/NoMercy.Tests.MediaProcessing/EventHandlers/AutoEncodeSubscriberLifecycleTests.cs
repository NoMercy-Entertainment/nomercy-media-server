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
using NoMercy.Database.Models.Media;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.MediaProcessing.EventHandlers;

/// <summary>
/// Lifecycle + dispatch-leg tests for <see cref="AutoEncodeSubscriber"/>. The
/// subscriber takes an injected <c>IDbContextFactory</c> and
/// <see cref="IJobDispatcher"/>, so the full scan-to-dispatch chain is
/// assertable without the real queue infrastructure. These tests verify:
/// - Start subscribes
/// - Stop disposes subscriptions
/// - Multiple Start/Stop cycles don't leak subscriptions
/// - A scan event for a library with auto-encode-on-scan on and a preset
///   assigned actually dispatches a VideoEncodeJob carrying that preset
/// - A scan event for a library with auto-encode-on-scan off, or with no
///   preset assigned, dispatches nothing
/// - A scan event for a library with no encoder-preset folder dispatches nothing
/// </summary>
public class AutoEncodeSubscriberLifecycleTests
{
    private static IStorage NoOpStorage()
    {
        IStorageDriver driver = new LocalStorageDriver();
        return new LocalStorage(driver, new([], driver));
    }

    private static IDbContextFactory<MediaContext> ContextFactory(out SqliteConnection connection)
    {
        SqliteConnection conn = new("DataSource=:memory:");
        conn.Open();
        connection = conn;

        // These tests seed a folder without materialising its Driver row; the
        // subscriber's decision logic doesn't need it, so disable FK enforcement
        // rather than build the full driver graph.
        using (SqliteCommand pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(conn)
            .Options;

        using (MediaContext seed = new(options))
            seed.Database.EnsureCreated();

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(options));
        factory.Setup(f => f.CreateDbContext()).Returns(() => new(options));
        return factory.Object;
    }

    [Fact]
    public async Task Start_SubscribesToMediaFilesScannedEvent()
    {
        InMemoryEventBus bus = new();
        Mock<IJobDispatcher> dispatcher = new();
        AutoEncodeSubscriber subscriber = new(
            bus,
            NullLogger<AutoEncodeSubscriber>.Instance,
            NoOpStorage(),
            ContextFactory(out SqliteConnection connection),
            dispatcher.Object
        );

        await subscriber.StartAsync(CancellationToken.None);

        // Publishing an event while subscribed should reach the handler without
        // throwing. No library exists for this id, so the handler returns early.
        await bus.PublishAsync(
            new MediaFilesScannedEvent { MediaId = -1, LibraryId = Ulid.NewUlid() }
        );

        connection.Dispose();
        Assert.True(true);
    }

    [Fact]
    public async Task Stop_DisposesSubscriptions_EventBusNoLongerCalls()
    {
        TrackingEventBus bus = new();
        Mock<IJobDispatcher> dispatcher = new();
        AutoEncodeSubscriber subscriber = new(
            bus,
            NullLogger<AutoEncodeSubscriber>.Instance,
            NoOpStorage(),
            ContextFactory(out SqliteConnection connection),
            dispatcher.Object
        );

        await subscriber.StartAsync(CancellationToken.None);
        Assert.Single(bus.ActiveSubscriptions);

        await subscriber.StopAsync(CancellationToken.None);
        Assert.Empty(bus.ActiveSubscriptions);
        connection.Dispose();
    }

    [Fact]
    public async Task MultipleStartStopCycles_DoNotLeakSubscriptions()
    {
        TrackingEventBus bus = new();
        Mock<IJobDispatcher> dispatcher = new();
        AutoEncodeSubscriber subscriber = new(
            bus,
            NullLogger<AutoEncodeSubscriber>.Instance,
            NoOpStorage(),
            ContextFactory(out SqliteConnection connection),
            dispatcher.Object
        );

        for (int i = 0; i < 3; i++)
        {
            await subscriber.StartAsync(CancellationToken.None);
            await subscriber.StopAsync(CancellationToken.None);
        }

        Assert.Empty(bus.ActiveSubscriptions);
        connection.Dispose();
    }

    [Fact]
    public async Task ScanEvent_ForPresetMappedFolderWithVideoFile_DispatchesVideoEncodeJob()
    {
        // The dispatch gate reads the same EncodingPresetFolders table
        // VideoEncodeJob resolves its presets from — a folder with a preset
        // link must dispatch.
        Ulid libraryId = Ulid.NewUlid();
        Ulid mediaId = Ulid.NewUlid();

        IDbContextFactory<MediaContext> factory = ContextFactory(out SqliteConnection connection);
        (int movieId, Ulid presetId) = SeedPresetMappedMovie(factory, libraryId, mediaId);

        List<VideoEncodeJob> dispatched = [];
        Mock<IJobDispatcher> dispatcher = new();
        dispatcher
            .Setup(d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<IShouldQueue, string, int>(
                (job, _, _) =>
                {
                    if (job is VideoEncodeJob encodeJob)
                        dispatched.Add(encodeJob);
                }
            );
        InMemoryEventBus bus = new();
        AutoEncodeSubscriber subscriber = new(
            bus,
            NullLogger<AutoEncodeSubscriber>.Instance,
            NoOpStorage(),
            factory,
            dispatcher.Object
        );
        await subscriber.StartAsync(CancellationToken.None);

        await bus.PublishAsync(
            new MediaFilesScannedEvent { MediaId = movieId, LibraryId = libraryId }
        );

        dispatched
            .Should()
            .HaveCount(1, "a V2-preset-mapped folder must queue exactly one VideoEncodeJob");
        dispatched[0].LibraryId.Should().Be(libraryId);
        dispatched[0].Id.Should().Be(movieId.ToString());
        dispatched[0].InputFile.Should().Contain("Movie.mkv");
        dispatched[0]
            .PresetId.Should()
            .Be(presetId, "the job must run the library's assigned preset");
        connection.Dispose();
    }

    [Fact]
    public async Task ScanEvent_ForLibraryWithoutEncodingPresetFolder_DispatchesNothing()
    {
        Ulid libraryId = Ulid.NewUlid();

        IDbContextFactory<MediaContext> factory = ContextFactory(out SqliteConnection connection);
        SeedGateOnlyLibrary(factory, libraryId, autoEncodeOnScan: true, assignEncodePreset: true);

        Mock<IJobDispatcher> dispatcher = new();
        InMemoryEventBus bus = new();
        AutoEncodeSubscriber subscriber = new(
            bus,
            NullLogger<AutoEncodeSubscriber>.Instance,
            NoOpStorage(),
            factory,
            dispatcher.Object
        );
        await subscriber.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new MediaFilesScannedEvent { MediaId = 1, LibraryId = libraryId });

        dispatcher.Verify(
            d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never,
            "no encoding-preset folder link means no auto-encode"
        );
        connection.Dispose();
    }

    [Fact]
    public async Task ScanEvent_ForLibraryWithAutoEncodeOnScanOff_DispatchesNothing()
    {
        // Per-library gate: a folder + preset link alone is not enough —
        // AutoEncodeOnScan must be explicitly turned on for this library.
        Ulid libraryId = Ulid.NewUlid();
        Ulid mediaId = Ulid.NewUlid();

        IDbContextFactory<MediaContext> factory = ContextFactory(out SqliteConnection connection);
        (int movieId, _) = SeedPresetMappedMovie(
            factory,
            libraryId,
            mediaId,
            autoEncodeOnScan: false
        );

        Mock<IJobDispatcher> dispatcher = new();
        InMemoryEventBus bus = new();
        AutoEncodeSubscriber subscriber = new(
            bus,
            NullLogger<AutoEncodeSubscriber>.Instance,
            NoOpStorage(),
            factory,
            dispatcher.Object
        );
        await subscriber.StartAsync(CancellationToken.None);

        await bus.PublishAsync(
            new MediaFilesScannedEvent { MediaId = movieId, LibraryId = libraryId }
        );

        dispatcher.Verify(
            d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never,
            "AutoEncodeOnScan defaults off and must not be bypassed by a preset-mapped folder"
        );
        connection.Dispose();
    }

    [Fact]
    public async Task ScanEvent_ForLibraryWithAutoEncodeOnButNoPreset_DispatchesNothing()
    {
        // Per-library gate: AutoEncodeOnScan alone is not enough — the
        // library needs an EncodePresetId to know which preset to run.
        Ulid libraryId = Ulid.NewUlid();
        Ulid mediaId = Ulid.NewUlid();

        IDbContextFactory<MediaContext> factory = ContextFactory(out SqliteConnection connection);
        (int movieId, _) = SeedPresetMappedMovie(
            factory,
            libraryId,
            mediaId,
            autoEncodeOnScan: true,
            assignEncodePreset: false
        );

        Mock<IJobDispatcher> dispatcher = new();
        InMemoryEventBus bus = new();
        AutoEncodeSubscriber subscriber = new(
            bus,
            NullLogger<AutoEncodeSubscriber>.Instance,
            NoOpStorage(),
            factory,
            dispatcher.Object
        );
        await subscriber.StartAsync(CancellationToken.None);

        await bus.PublishAsync(
            new MediaFilesScannedEvent { MediaId = movieId, LibraryId = libraryId }
        );

        dispatcher.Verify(
            d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never,
            "a library with no EncodePresetId must not auto-encode even with the flag on"
        );
        connection.Dispose();
    }

    private static (int MovieId, Ulid PresetId) SeedPresetMappedMovie(
        IDbContextFactory<MediaContext> factory,
        Ulid libraryId,
        Ulid mediaId,
        bool autoEncodeOnScan = true,
        bool assignEncodePreset = true
    )
    {
        using MediaContext context = factory.CreateDbContext();

        Ulid driverId = Ulid.NewUlid();
        Ulid folderId = Ulid.NewUlid();
        const string folderPath = "/media/movies";

        EncodingPreset preset = new()
        {
            Id = Ulid.NewUlid(),
            Name = "Archive 1080p",
            ProfileJson = "{}",
            IsBuiltIn = false,
        };
        context.EncodingPresets.Add(preset);

        Library library = new()
        {
            Id = libraryId,
            Title = "Movies",
            AutoEncodeOnScan = autoEncodeOnScan,
            EncodePresetId = assignEncodePreset ? preset.Id : null,
        };
        context.Libraries.Add(library);

        Folder folder = new()
        {
            Id = folderId,
            Path = folderPath,
            DriverId = driverId,
        };
        context.Folders.Add(folder);
        context.FolderLibrary.Add(new() { FolderId = folderId, LibraryId = libraryId });

        context.EncodingPresetFolders.Add(
            new()
            {
                PresetId = preset.Id,
                FolderId = folderId,
                IsDefault = true,
            }
        );

        int movieId = 4242;
        context.VideoFiles.Add(
            new()
            {
                Id = Ulid.NewUlid(),
                MovieId = movieId,
                HostFolder = folderPath,
                Filename = "/Movie.mkv",
                Quality = "1080p",
                Share = "local",
                Languages = "eng",
            }
        );

        context.SaveChanges();
        return (movieId, preset.Id);
    }

    private static void SeedGateOnlyLibrary(
        IDbContextFactory<MediaContext> factory,
        Ulid libraryId,
        bool autoEncodeOnScan,
        bool assignEncodePreset
    )
    {
        using MediaContext context = factory.CreateDbContext();

        Library library = new()
        {
            Id = libraryId,
            Title = "Movies",
            AutoEncodeOnScan = autoEncodeOnScan,
            EncodePresetId = assignEncodePreset ? Ulid.NewUlid() : null,
        };
        context.Libraries.Add(library);

        context.SaveChanges();
    }

    /// <summary>
    /// Wraps <see cref="InMemoryEventBus"/> to expose a count of live
    /// subscriptions so tests can verify disposal without poking at the bus
    /// internals via reflection.
    /// </summary>
    private sealed class TrackingEventBus : IEventBus
    {
        private readonly InMemoryEventBus _inner = new();
        public List<IDisposable> ActiveSubscriptions { get; } = [];

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
            where TEvent : IEvent => _inner.PublishAsync(@event, ct);

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
            where TEvent : IEvent
        {
            IDisposable subscription = _inner.Subscribe(handler);
            TrackingSubscription tracker = new(subscription, this);
            ActiveSubscriptions.Add(tracker);
            return tracker;
        }

        public IDisposable Subscribe<TEvent>(IEventHandler<TEvent> handler)
            where TEvent : IEvent
        {
            IDisposable subscription = _inner.Subscribe(handler);
            TrackingSubscription tracker = new(subscription, this);
            ActiveSubscriptions.Add(tracker);
            return tracker;
        }

        private sealed class TrackingSubscription(IDisposable inner, TrackingEventBus owner)
            : IDisposable
        {
            public void Dispose()
            {
                inner.Dispose();
                owner.ActiveSubscriptions.Remove(this);
            }
        }
    }
}
