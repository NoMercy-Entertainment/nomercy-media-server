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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.Service.Subscribers;
using NoMercy.Tests.Service.TestHelpers;

namespace NoMercy.Tests.Service.Jobs;

/// <summary>
/// Analysis has to happen as part of an import or rescan, with no switch to
/// find — so the moment a music library finishes scanning, its outstanding
/// tracks go on the queue. These tests pin that, the two cases that must stay
/// quiet, and the promise that a failure in here never takes the scan down
/// with it.
/// </summary>
public class AudioAnalysisSubscriberTests
{
    private static async Task<Ulid> SeedLibrary(
        IDbContextFactory<MediaContext> contextFactory,
        bool analyzeAudio,
        string type = "music"
    )
    {
        Ulid libraryId = Ulid.NewUlid();

        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        context.Libraries.Add(
            new Library
            {
                Id = libraryId,
                Title = "A Library",
                Type = type,
                AnalyzeAudio = analyzeAudio,
            }
        );
        await context.SaveChangesAsync();

        return libraryId;
    }

    private static LibraryScanCompletedEvent ScanOf(Ulid libraryId) =>
        new()
        {
            LibraryId = libraryId,
            LibraryName = "A Library",
            ItemsFound = 12,
            Duration = TimeSpan.FromSeconds(3),
        };

    private static Mock<IAudioAnalysisScheduler> SchedulerReturning(
        int queued,
        List<Ulid> scheduled
    )
    {
        Mock<IAudioAnalysisScheduler> scheduler = new();
        scheduler
            .Setup(s =>
                s.QueueAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>())
            )
            .Callback<IReadOnlyCollection<Ulid>, CancellationToken>(
                (libraryIds, _) => scheduled.AddRange(libraryIds)
            )
            .ReturnsAsync(queued);

        return scheduler;
    }

    [Fact]
    public async Task ScanCompleted_ForAnOptedInMusicLibrary_QueuesThatLibrary()
    {
        await using SqliteMediaContextFactory contextFactory = new();
        Ulid libraryId = await SeedLibrary(contextFactory, analyzeAudio: true);

        List<Ulid> scheduled = [];
        InMemoryEventBus bus = new();
        AudioAnalysisSubscriber subscriber = new(
            bus,
            SchedulerReturning(7, scheduled).Object,
            contextFactory,
            NullLogger<AudioAnalysisSubscriber>.Instance
        );

        await subscriber.StartAsync(CancellationToken.None);
        await bus.PublishAsync(ScanOf(libraryId));

        Assert.Equal([libraryId], scheduled);
    }

    [Fact]
    public async Task ScanCompleted_ForALibraryThatOptedOut_QueuesNothing()
    {
        await using SqliteMediaContextFactory contextFactory = new();
        Ulid libraryId = await SeedLibrary(contextFactory, analyzeAudio: false);

        List<Ulid> scheduled = [];
        InMemoryEventBus bus = new();
        AudioAnalysisSubscriber subscriber = new(
            bus,
            SchedulerReturning(0, scheduled).Object,
            contextFactory,
            NullLogger<AudioAnalysisSubscriber>.Instance
        );

        await subscriber.StartAsync(CancellationToken.None);
        await bus.PublishAsync(ScanOf(libraryId));

        Assert.Empty(scheduled);
    }

    [Fact]
    public async Task ScanCompleted_ForAVideoLibrary_QueuesNothing()
    {
        await using SqliteMediaContextFactory contextFactory = new();
        Ulid libraryId = await SeedLibrary(contextFactory, analyzeAudio: true, type: "movie");

        List<Ulid> scheduled = [];
        InMemoryEventBus bus = new();
        AudioAnalysisSubscriber subscriber = new(
            bus,
            SchedulerReturning(0, scheduled).Object,
            contextFactory,
            NullLogger<AudioAnalysisSubscriber>.Instance
        );

        await subscriber.StartAsync(CancellationToken.None);
        await bus.PublishAsync(ScanOf(libraryId));

        Assert.Empty(scheduled);
    }

    [Fact]
    public async Task ScanCompleted_ForALibraryThatIsGone_QueuesNothing()
    {
        await using SqliteMediaContextFactory contextFactory = new();

        List<Ulid> scheduled = [];
        InMemoryEventBus bus = new();
        AudioAnalysisSubscriber subscriber = new(
            bus,
            SchedulerReturning(0, scheduled).Object,
            contextFactory,
            NullLogger<AudioAnalysisSubscriber>.Instance
        );

        await subscriber.StartAsync(CancellationToken.None);
        await bus.PublishAsync(ScanOf(Ulid.NewUlid()));

        Assert.Empty(scheduled);
    }

    /// <summary>
    /// An event handler that throws takes the publisher with it. A queue that
    /// cannot be reached must cost the user their analysis, never their scan —
    /// so the handler itself is asserted directly, not through the bus, which
    /// would swallow the exception on its own and prove nothing.
    /// </summary>
    [Fact]
    public async Task ScanCompleted_WhenTheSchedulerThrows_IsLoggedAndNotPropagated()
    {
        await using SqliteMediaContextFactory contextFactory = new();
        Ulid libraryId = await SeedLibrary(contextFactory, analyzeAudio: true);

        Mock<IAudioAnalysisScheduler> scheduler = new();
        scheduler
            .Setup(s =>
                s.QueueAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new InvalidOperationException("the queue is unreachable"));

        RecordingLogger logger = new();
        AudioAnalysisSubscriber subscriber = new(
            new InMemoryEventBus(),
            scheduler.Object,
            contextFactory,
            logger
        );

        await subscriber.OnLibraryScanCompleted(ScanOf(libraryId), CancellationToken.None);

        Assert.Contains(LogLevel.Error, logger.Levels);
    }

    [Fact]
    public async Task Stop_DisposesTheSubscription_SoALaterScanQueuesNothing()
    {
        await using SqliteMediaContextFactory contextFactory = new();
        Ulid libraryId = await SeedLibrary(contextFactory, analyzeAudio: true);

        List<Ulid> scheduled = [];
        InMemoryEventBus bus = new();
        AudioAnalysisSubscriber subscriber = new(
            bus,
            SchedulerReturning(1, scheduled).Object,
            contextFactory,
            NullLogger<AudioAnalysisSubscriber>.Instance
        );

        await subscriber.StartAsync(CancellationToken.None);
        await subscriber.StopAsync(CancellationToken.None);
        await bus.PublishAsync(ScanOf(libraryId));

        Assert.Empty(scheduled);
    }

    [Fact]
    public async Task MultipleStartStopCycles_DoNotQueueTheSameLibraryTwice()
    {
        await using SqliteMediaContextFactory contextFactory = new();
        Ulid libraryId = await SeedLibrary(contextFactory, analyzeAudio: true);

        List<Ulid> scheduled = [];
        InMemoryEventBus bus = new();
        AudioAnalysisSubscriber subscriber = new(
            bus,
            SchedulerReturning(1, scheduled).Object,
            contextFactory,
            NullLogger<AudioAnalysisSubscriber>.Instance
        );

        for (int cycle = 0; cycle < 3; cycle++)
        {
            await subscriber.StartAsync(CancellationToken.None);
            await subscriber.StopAsync(CancellationToken.None);
        }

        await subscriber.StartAsync(CancellationToken.None);
        await bus.PublishAsync(ScanOf(libraryId));

        Assert.Equal([libraryId], scheduled);
    }

    private sealed class RecordingLogger : ILogger<AudioAnalysisSubscriber>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Levels.Add(logLevel);
        }
    }
}
