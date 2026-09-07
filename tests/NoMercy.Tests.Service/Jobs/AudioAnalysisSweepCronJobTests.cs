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
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Service.Jobs;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.Service.Jobs;

public class AudioAnalysisSweepCronJobTests : IDisposable
{
    private const int AnalyzerVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AudioAnalysisSweepCronJobTests()
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

    private Ulid SeedLibrary(bool analyzeAudio, string type = "music")
    {
        Ulid libraryId = Ulid.NewUlid();

        using MediaContext context = new(_options);
        context.Libraries.Add(
            new Library
            {
                Id = libraryId,
                Title = "A Library",
                Type = type,
                AnalyzeAudio = analyzeAudio,
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

    private (AudioAnalysisSweepCronJob Job, List<Guid> Queued) CreateSweep()
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

        AudioAnalysisSweepCronJob job = new(
            dispatcher.Object,
            analyzer.Object,
            factory.Object,
            NullLogger<AudioAnalysisSweepCronJob>.Instance
        );

        return (job, queued);
    }

    [Fact]
    public async Task Execute_QueuesTracksThatHaveNoAnalysis()
    {
        Ulid libraryId = SeedLibrary(analyzeAudio: true);
        Guid trackId = SeedTrack(libraryId, state: null);

        (AudioAnalysisSweepCronJob job, List<Guid> queued) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        Assert.Equal([trackId], queued);
    }

    /// <summary>
    /// The opt-in is the whole consent model for this feature. A library that
    /// never asked must never have its tracks analyzed.
    /// </summary>
    [Fact]
    public async Task Execute_SkipsLibrariesThatDidNotOptIn()
    {
        Ulid libraryId = SeedLibrary(analyzeAudio: false);
        SeedTrack(libraryId, state: null);

        (AudioAnalysisSweepCronJob job, List<Guid> queued) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        Assert.Empty(queued);
    }

    [Fact]
    public async Task Execute_SkipsNonMusicLibraries()
    {
        Ulid libraryId = SeedLibrary(analyzeAudio: true, type: "movie");
        SeedTrack(libraryId, state: null);

        (AudioAnalysisSweepCronJob job, List<Guid> queued) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        Assert.Empty(queued);
    }

    [Fact]
    public async Task Execute_SkipsTracksThisVersionAlreadyAnalyzed()
    {
        Ulid libraryId = SeedLibrary(analyzeAudio: true);
        SeedTrack(libraryId, AudioAnalysisState.Ok);

        (AudioAnalysisSweepCronJob job, List<Guid> queued) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        Assert.Empty(queued);
    }

    /// <summary>
    /// A terminal failure at the current version is an answer. Re-queuing it
    /// every hour would spend the queue on files that cannot succeed.
    /// </summary>
    [Fact]
    public async Task Execute_SkipsTracksThatFailedAtThisVersion()
    {
        Ulid libraryId = SeedLibrary(analyzeAudio: true);
        SeedTrack(libraryId, AudioAnalysisState.Failed);

        (AudioAnalysisSweepCronJob job, List<Guid> queued) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        Assert.Empty(queued);
    }

    [Fact]
    public async Task Execute_RequeuesTracksLeftPendingByAnUnfinishedRun()
    {
        Ulid libraryId = SeedLibrary(analyzeAudio: true);
        Guid trackId = SeedTrack(libraryId, AudioAnalysisState.Pending);

        (AudioAnalysisSweepCronJob job, List<Guid> queued) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        Assert.Equal([trackId], queued);
    }

    /// <summary>
    /// The reason the version column exists: improving the analyzer re-queues
    /// exactly the stale rows, without a full library rescan.
    /// </summary>
    [Fact]
    public async Task Execute_RequeuesTracksAnalyzedByAnOlderVersion()
    {
        Ulid libraryId = SeedLibrary(analyzeAudio: true);
        Guid trackId = SeedTrack(libraryId, AudioAnalysisState.Ok, version: AnalyzerVersion - 1);

        (AudioAnalysisSweepCronJob job, List<Guid> queued) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        Assert.Equal([trackId], queued);
    }
}
