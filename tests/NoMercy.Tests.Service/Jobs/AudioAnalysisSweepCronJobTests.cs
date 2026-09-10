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
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.Service.Jobs;

namespace NoMercy.Tests.Service.Jobs;

/// <summary>
/// The sweep's own job is now the library question — which libraries want
/// their audio analyzed — and handing that set to the scheduler. Which tracks
/// inside those libraries still need a verdict is proven in
/// <see cref="AudioAnalysisSchedulerTests" />.
/// </summary>
public class AudioAnalysisSweepCronJobTests : IDisposable
{
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

    private (AudioAnalysisSweepCronJob Job, List<Ulid> Scheduled) CreateSweep()
    {
        List<Ulid> scheduled = [];

        Mock<IAudioAnalysisScheduler> scheduler = new();
        scheduler
            .Setup(s =>
                s.QueueAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>())
            )
            .Callback<IReadOnlyCollection<Ulid>, CancellationToken>(
                (libraryIds, _) => scheduled.AddRange(libraryIds)
            )
            .ReturnsAsync(0);

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        AudioAnalysisSweepCronJob job = new(scheduler.Object, factory.Object);

        return (job, scheduled);
    }

    [Fact]
    public async Task Execute_SchedulesTheMusicLibrariesThatOptedIn()
    {
        Ulid libraryId = SeedLibrary(analyzeAudio: true);

        (AudioAnalysisSweepCronJob job, List<Ulid> scheduled) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        Assert.Equal([libraryId], scheduled);
    }

    /// <summary>
    /// The opt-out is the whole consent model for this feature. A library that
    /// turned it off must never have its tracks analyzed.
    /// </summary>
    [Fact]
    public async Task Execute_SkipsLibrariesThatOptedOut()
    {
        SeedLibrary(analyzeAudio: false);

        (AudioAnalysisSweepCronJob job, List<Ulid> scheduled) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        Assert.Empty(scheduled);
    }

    [Fact]
    public async Task Execute_SkipsNonMusicLibraries()
    {
        SeedLibrary(analyzeAudio: true, type: "movie");

        (AudioAnalysisSweepCronJob job, List<Ulid> scheduled) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        Assert.Empty(scheduled);
    }

    [Fact]
    public async Task Execute_SchedulesEveryOptedInMusicLibraryTogether()
    {
        Ulid first = SeedLibrary(analyzeAudio: true);
        Ulid second = SeedLibrary(analyzeAudio: true);
        SeedLibrary(analyzeAudio: false);
        SeedLibrary(analyzeAudio: true, type: "tv");

        (AudioAnalysisSweepCronJob job, List<Ulid> scheduled) = CreateSweep();
        await job.ExecuteAsync(string.Empty);

        // Both sides sorted. Two ULIDs minted in the same millisecond differ
        // only in their random tail, so the order they were created in is not
        // the order they sort in, and comparing a seed-ordered list against a
        // sorted one passes or fails by luck.
        Assert.Equal(new List<Ulid> { first, second }.Order(), scheduled.Order());
    }
}
