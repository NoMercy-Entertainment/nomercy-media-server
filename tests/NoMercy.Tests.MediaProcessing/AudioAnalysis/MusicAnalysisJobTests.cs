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
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.AudioAnalysis;

public class MusicAnalysisJobTests : IDisposable
{
    private const int AnalyzerVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Guid _trackId = Guid.NewGuid();

    public MusicAnalysisJobTests()
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

        context.Tracks.Add(
            new Track
            {
                Id = _trackId,
                Name = "A Track",
                HostFolder = "/music/album",
                Filename = "/track.flac",
            }
        );

        context.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private MusicAnalysisJob CreateJob(
        AudioAnalysisResult? result,
        Mock<IAudioAnalyzer>? analyzerMock = null
    )
    {
        Mock<IAudioAnalyzer> analyzer = analyzerMock ?? new Mock<IAudioAnalyzer>();
        analyzer.SetupGet(a => a.Version).Returns(AnalyzerVersion);
        analyzer
            .Setup(a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        Mock<IStorageDriver> storageDriver = new();
        storageDriver
            .Setup(s => s.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns<string, string[]>((folder, segments) => folder + string.Concat(segments));

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        return new MusicAnalysisJob(
            analyzer.Object,
            storageDriver.Object,
            factory.Object,
            NullLoggerFactory.Instance
        )
        {
            TrackId = _trackId,
        };
    }

    private static AudioAnalysisResult SampleResult() =>
        new()
        {
            Bpm = 128.0,
            KeyName = "Am",
            KeyConfidence = 0.8,
            IntegratedLufs = -9.0,
            SpectralCentroid = 2400.0,
            IntroEndMs = 1500,
            OutroStartMs = 41376,
        };

    private TrackAudioAnalysis? ReadRow()
    {
        using MediaContext context = new(_options);
        return context.TrackAudioAnalysis.AsNoTracking().FirstOrDefault(a => a.TrackId == _trackId);
    }

    [Fact]
    public async Task Handle_PersistsTheMeasurements()
    {
        await CreateJob(SampleResult()).Handle();

        TrackAudioAnalysis? row = ReadRow();

        Assert.NotNull(row);
        Assert.Equal(AudioAnalysisState.Ok, row.State);
        Assert.Equal(128.0, row.Bpm);
        Assert.Equal("Am", row.KeyName);
        Assert.Equal(AnalyzerVersion, row.AnalyzerVersion);
    }

    [Fact]
    public async Task Handle_DerivesCamelotFromTheDetectedKey()
    {
        await CreateJob(SampleResult()).Handle();

        Assert.Equal("8A", ReadRow()?.KeyCamelot);
    }

    [Fact]
    public async Task Handle_DerivesEnergyFromTheStoredMeasurements()
    {
        await CreateJob(SampleResult()).Handle();

        double? energy = ReadRow()?.Energy;

        // A concrete number, not AudioEnergy.Estimate(...) compared to itself —
        // that form passes for every possible formula. AudioEnergyTests pins the
        // formula; this pins that the job actually applies it to the values it
        // stored.
        Assert.NotNull(energy);
        Assert.Equal(0.693778, energy.Value, 6);
    }

    /// <summary>
    /// A file that yields nothing must reach a terminal state. Left Pending, it
    /// is selected by every later sweep and the queue never drains.
    /// </summary>
    [Fact]
    public async Task Handle_RecordsAFailureRatherThanLeavingTheRowPending()
    {
        await CreateJob(null).Handle();

        TrackAudioAnalysis? row = ReadRow();

        Assert.NotNull(row);
        Assert.Equal(AudioAnalysisState.Failed, row.State);
        Assert.False(string.IsNullOrWhiteSpace(row.FailureReason));
    }

    [Fact]
    public async Task Handle_DoesNotAnalyzeATrackThisVersionAlreadyDid()
    {
        await CreateJob(SampleResult()).Handle();

        Mock<IAudioAnalyzer> second = new();
        await CreateJob(SampleResult(), second).Handle();

        second.Verify(
            a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Handle_ReanalyzesWhenTheAnalyzerVersionMoved()
    {
        await CreateJob(SampleResult()).Handle();

        Mock<IAudioAnalyzer> newer = new();
        newer.SetupGet(a => a.Version).Returns(AnalyzerVersion + 1);
        newer
            .Setup(a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleResult() with { Bpm = 174.0 });

        Mock<IStorageDriver> storageDriver = new();
        storageDriver
            .Setup(s => s.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns<string, string[]>((folder, segments) => folder + string.Concat(segments));

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        MusicAnalysisJob job = new(
            newer.Object,
            storageDriver.Object,
            factory.Object,
            NullLoggerFactory.Instance
        )
        {
            TrackId = _trackId,
        };

        await job.Handle();

        TrackAudioAnalysis? row = ReadRow();

        Assert.Equal(174.0, row?.Bpm);
        Assert.Equal(AnalyzerVersion + 1, row?.AnalyzerVersion);
    }

    [Fact]
    public async Task Handle_WritesOneRowWhenRunTwice()
    {
        await CreateJob(SampleResult()).Handle();
        await CreateJob(SampleResult()).Handle();

        using MediaContext context = new(_options);

        Assert.Equal(1, context.TrackAudioAnalysis.Count(a => a.TrackId == _trackId));
    }

    [Fact]
    public async Task Handle_IgnoresATrackThatIsNotThere()
    {
        MusicAnalysisJob job = CreateJob(SampleResult());
        job.TrackId = Guid.NewGuid();

        await job.Handle();

        using MediaContext context = new(_options);

        Assert.Empty(context.TrackAudioAnalysis);
    }
}
