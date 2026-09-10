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
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Service.Jobs;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.Service.Jobs;

/// <summary>
/// The scheduler is the one place that answers "which tracks still need a
/// verdict" and puts them on the queue. Both the hourly sweep and the
/// scan-completed hook go through it, so its behaviour is proven once here
/// instead of twice at the call sites.
/// </summary>
public class AudioAnalysisSchedulerTests : IDisposable
{
    private const int AnalyzerVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AudioAnalysisSchedulerTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Ulid SeedLibrary(string type = "music")
    {
        Ulid libraryId = Ulid.NewUlid();

        using MediaContext context = new(_options);
        context.Libraries.Add(
            new Library
            {
                Id = libraryId,
                Title = "A Library",
                Type = type,
                AnalyzeAudio = true,
            }
        );
        context.SaveChanges();

        return libraryId;
    }

    private Guid SeedTrack(Ulid libraryId, AudioAnalysisState? state, int version = AnalyzerVersion)
    {
        Guid trackId = Guid.NewGuid();

        using MediaContext context = new(_options);
        context.Tracks.Add(new Track { Id = trackId, Name = "A Track" });
        context.LibraryTrack.Add(new LibraryTrack { LibraryId = libraryId, TrackId = trackId });

        if (state is not null)
        {
            context.TrackAudioAnalysis.Add(
                new TrackAudioAnalysis
                {
                    TrackId = trackId,
                    AnalyzerVersion = version,
                    State = state.Value,
                    AnalyzedAt = DateTime.UtcNow,
                }
            );
        }

        context.SaveChanges();

        return trackId;
    }

    private List<Guid> SeedTracks(Ulid libraryId, int count)
    {
        List<Guid> trackIds = [];

        using MediaContext context = new(_options);

        for (int index = 0; index < count; index++)
        {
            Guid trackId = Guid.NewGuid();
            trackIds.Add(trackId);

            context.Tracks.Add(new Track { Id = trackId, Name = "A Track" });
            context.LibraryTrack.Add(new LibraryTrack { LibraryId = libraryId, TrackId = trackId });
        }

        context.SaveChanges();

        return trackIds;
    }

    private void RecordVerdicts(IEnumerable<Guid> trackIds)
    {
        using MediaContext context = new(_options);

        foreach (Guid trackId in trackIds)
        {
            context.TrackAudioAnalysis.Add(
                new TrackAudioAnalysis
                {
                    TrackId = trackId,
                    AnalyzerVersion = AnalyzerVersion,
                    State = AudioAnalysisState.Ok,
                    AnalyzedAt = DateTime.UtcNow,
                }
            );
        }

        context.SaveChanges();
    }

    private (AudioAnalysisScheduler Scheduler, List<Guid> Queued) CreateScheduler()
    {
        List<Guid> queued = [];

        Mock<IJobDispatcher> dispatcher = new();
        dispatcher
            .Setup(d => d.Dispatch(It.IsAny<IShouldQueue>()))
            .Callback<IShouldQueue>(job =>
            {
                if (job is MusicAnalysisJob analysis)
                {
                    queued.Add(analysis.TrackId);
                }
            });

        Mock<IAudioAnalyzer> analyzer = new();
        analyzer.SetupGet(a => a.Version).Returns(AnalyzerVersion);

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        AudioAnalysisScheduler scheduler = new(
            dispatcher.Object,
            analyzer.Object,
            factory.Object,
            NullLogger<AudioAnalysisScheduler>.Instance
        );

        return (scheduler, queued);
    }

    [Fact]
    public async Task QueueAsync_QueuesTracksWithoutAVerdict_AndReturnsTheCount()
    {
        Ulid libraryId = SeedLibrary();
        Guid trackId = SeedTrack(libraryId, state: null);

        (AudioAnalysisScheduler scheduler, List<Guid> queued) = CreateScheduler();
        int count = await scheduler.QueueAsync([libraryId]);

        Assert.Equal([trackId], queued);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task QueueAsync_SkipsTracksThisVersionAlreadyAnalyzed()
    {
        Ulid libraryId = SeedLibrary();
        SeedTrack(libraryId, AudioAnalysisState.Ok);

        (AudioAnalysisScheduler scheduler, List<Guid> queued) = CreateScheduler();
        int count = await scheduler.QueueAsync([libraryId]);

        Assert.Empty(queued);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// A terminal failure at the current version is an answer. Re-queuing it
    /// every hour would spend the queue on files that cannot succeed.
    /// </summary>
    [Fact]
    public async Task QueueAsync_SkipsTracksThatFailedAtThisVersion()
    {
        Ulid libraryId = SeedLibrary();
        SeedTrack(libraryId, AudioAnalysisState.Failed);

        (AudioAnalysisScheduler scheduler, List<Guid> queued) = CreateScheduler();

        Assert.Equal(0, await scheduler.QueueAsync([libraryId]));
        Assert.Empty(queued);
    }

    [Fact]
    public async Task QueueAsync_RequeuesTracksLeftPendingByAnUnfinishedRun()
    {
        Ulid libraryId = SeedLibrary();
        Guid trackId = SeedTrack(libraryId, AudioAnalysisState.Pending);

        (AudioAnalysisScheduler scheduler, List<Guid> queued) = CreateScheduler();

        Assert.Equal(1, await scheduler.QueueAsync([libraryId]));
        Assert.Equal([trackId], queued);
    }

    /// <summary>
    /// The reason the version column exists: improving the analyzer re-queues
    /// exactly the stale rows, without a full library rescan.
    /// </summary>
    [Fact]
    public async Task QueueAsync_RequeuesTracksAnalyzedByAnOlderVersion()
    {
        Ulid libraryId = SeedLibrary();
        Guid trackId = SeedTrack(libraryId, AudioAnalysisState.Ok, version: AnalyzerVersion - 1);

        (AudioAnalysisScheduler scheduler, List<Guid> queued) = CreateScheduler();

        Assert.Equal(1, await scheduler.QueueAsync([libraryId]));
        Assert.Equal([trackId], queued);
    }

    [Fact]
    public async Task QueueAsync_QueuesNothingForAnEmptyLibrarySet()
    {
        Ulid libraryId = SeedLibrary();
        SeedTrack(libraryId, state: null);

        (AudioAnalysisScheduler scheduler, List<Guid> queued) = CreateScheduler();
        int count = await scheduler.QueueAsync([]);

        Assert.Empty(queued);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task QueueAsync_QueuesTracksFromEveryLibraryItIsGiven()
    {
        Ulid first = SeedLibrary();
        Ulid second = SeedLibrary();
        Guid firstTrack = SeedTrack(first, state: null);
        Guid secondTrack = SeedTrack(second, state: null);

        (AudioAnalysisScheduler scheduler, List<Guid> queued) = CreateScheduler();
        int count = await scheduler.QueueAsync([first, second]);

        Assert.Equal(2, count);
        Assert.Equal(new List<Guid> { firstTrack, secondTrack }.Order(), queued.Order());
    }

    /// <summary>
    /// A page is 500 and dispatching writes nothing back, so a scheduler that
    /// re-asks the same question without moving forward would hand the queue
    /// the same 500 tracks for ever and never reach the rest of the library.
    /// </summary>
    [Fact]
    public async Task QueueAsync_PagesPastTheBatchSize()
    {
        Ulid libraryId = SeedLibrary();
        List<Guid> trackIds = SeedTracks(libraryId, 1201);

        (AudioAnalysisScheduler scheduler, List<Guid> queued) = CreateScheduler();
        int count = await scheduler.QueueAsync([libraryId]);

        Assert.Equal(1201, count);
        Assert.Equal(trackIds.Order().ToList(), queued.Order().ToList());
    }

    /// <summary>
    /// Both callers may run minutes apart on the same library. Once the tracks
    /// carry a verdict there is nothing left to queue.
    /// </summary>
    [Fact]
    public async Task QueueAsync_QueuesNothingOnceEveryTrackHasAVerdict()
    {
        Ulid libraryId = SeedLibrary();
        List<Guid> trackIds = SeedTracks(libraryId, 3);

        (AudioAnalysisScheduler scheduler, List<Guid> queued) = CreateScheduler();
        Assert.Equal(3, await scheduler.QueueAsync([libraryId]));

        RecordVerdicts(trackIds);
        queued.Clear();

        Assert.Equal(0, await scheduler.QueueAsync([libraryId]));
        Assert.Empty(queued);
    }
}
