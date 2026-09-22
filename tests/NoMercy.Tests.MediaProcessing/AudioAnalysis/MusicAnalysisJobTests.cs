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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Events;
using NoMercy.Events.Music;
using NoMercy.Events.Plugins;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.AudioAnalysis;

public class MusicAnalysisJobTests : IDisposable
{
    private const int AnalyzerVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Guid _trackId = Guid.NewGuid();

    // The track is in two libraries: the completion event has to name both,
    // because the derived-audio retention policy is decided per library.
    private readonly Ulid _libraryOneId = Ulid.NewUlid();
    private readonly Ulid _libraryTwoId = Ulid.NewUlid();

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

        context.LibraryTrack.AddRange(
            new LibraryTrack(_libraryOneId, _trackId),
            new LibraryTrack(_libraryTwoId, _trackId)
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
        Mock<IAudioAnalyzer>? analyzerMock = null,
        Mock<IEventBus>? eventBus = null
    )
    {
        Mock<IAudioAnalyzer> analyzer = analyzerMock ?? new Mock<IAudioAnalyzer>();
        analyzer.SetupGet(a => a.Version).Returns(AnalyzerVersion);
        analyzer
            .Setup(a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        return CreateJobFrom(analyzer, eventBus);
    }

    private MusicAnalysisJob CreateJobFrom(
        Mock<IAudioAnalyzer> analyzer,
        Mock<IEventBus>? eventBus = null,
        bool fileExists = true,
        ILoggerFactory? loggerFactory = null
    )
    {
        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        Mock<IEventBus> bus = eventBus ?? new Mock<IEventBus>();

        return new MusicAnalysisJob(
            analyzer.Object,
            StorageDriver(fileExists).Object,
            factory.Object,
            loggerFactory ?? NullLoggerFactory.Instance,
            bus.Object
        )
        {
            TrackId = _trackId,
        };
    }

    // The job only analyses a file it can see, so every case that is not about
    // a missing file says the file is there.
    private static Mock<IStorageDriver> StorageDriver(bool fileExists = true)
    {
        Mock<IStorageDriver> storageDriver = new();
        storageDriver
            .Setup(s => s.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns<string, string[]>((folder, segments) => folder + string.Concat(segments));
        storageDriver.Setup(s => s.FileExists(It.IsAny<string>())).Returns(fileExists);
        return storageDriver;
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

    /// <summary>
    /// A file the analyzer cannot even open throws instead of returning null.
    /// That exception must not leave the job: with no row written, the sweep
    /// selects the track again every hour and the queue dead-letters it every
    /// hour, for good.
    /// </summary>
    [Fact]
    public async Task Handle_RecordsAFailureWhenTheAnalyzerThrows()
    {
        Mock<IAudioAnalyzer> analyzer = new();
        analyzer.SetupGet(a => a.Version).Returns(AnalyzerVersion);
        analyzer
            .Setup(a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("file vanished"));

        await CreateJobFrom(analyzer).Handle();

        TrackAudioAnalysis? row = ReadRow();

        Assert.NotNull(row);
        Assert.Equal(AudioAnalysisState.Failed, row.State);
        Assert.Equal(AnalyzerVersion, row.AnalyzerVersion);
        Assert.Contains("file vanished", row.FailureReason);
    }

    /// <summary>
    /// A track whose stored columns combine into a path that is not there — the
    /// doubled host folder an older import wrote, a mount that is gone — must
    /// not reach the analyzer at all, and the warning has to name both columns
    /// separately. The analyzer's own error names only the combined path, which
    /// for a doubled host folder reads as one odd string and tells nobody which
    /// column is wrong.
    /// </summary>
    [Fact]
    public async Task Handle_NamesHostFolderAndFilenameSeparatelyWhenTheFileIsNotThere()
    {
        RecordingLoggerFactory loggers = new();
        Mock<IAudioAnalyzer> analyzer = new();
        analyzer.SetupGet(a => a.Version).Returns(AnalyzerVersion);

        await CreateJobFrom(analyzer, fileExists: false, loggerFactory: loggers).Handle();

        analyzer.Verify(
            a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );

        string warning = Assert.Single(loggers.Warnings);
        Assert.Contains("/music/album", warning);
        Assert.Contains("/track.flac", warning);
    }

    [Fact]
    public async Task Handle_RecordsAFailureWhenTheFileIsNotThere()
    {
        Mock<IAudioAnalyzer> analyzer = new();
        analyzer.SetupGet(a => a.Version).Returns(AnalyzerVersion);

        await CreateJobFrom(analyzer, fileExists: false).Handle();

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

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        MusicAnalysisJob job = new(
            newer.Object,
            StorageDriver().Object,
            factory.Object,
            NullLoggerFactory.Instance,
            new Mock<IEventBus>().Object
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

    [Fact]
    public async Task AnOkVerdict_PublishesACompletedEvent()
    {
        Mock<IEventBus> bus = new();

        await CreateJob(SampleResult(), eventBus: bus).Handle();

        bus.Verify(
            b =>
                b.PublishAsync(
                    It.Is<TrackAudioAnalysisCompletedEvent>(e =>
                        e.TrackId == _trackId
                        && e.State == "Ok"
                        && e.AnalyzerVersion == AnalyzerVersion
                        && e.LibraryIds.Count == 2
                        && e.LibraryIds.Contains(_libraryOneId)
                        && e.LibraryIds.Contains(_libraryTwoId)
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    /// <summary>
    /// The verdict is already stored by the time the event goes out, so a bus
    /// that throws there must not take the job down with it: the queue would
    /// dead-letter a job whose row landed, and every later sweep would see a
    /// track that has its answer already.
    /// </summary>
    [Fact]
    public async Task AVerdictIsKept_WhenTheEventCannotBePublished()
    {
        Mock<IEventBus> bus = new();
        bus.Setup(b =>
                b.PublishAsync(
                    It.IsAny<TrackAudioAnalysisCompletedEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("the bus is gone"));

        MusicAnalysisJob job = CreateJob(SampleResult(), eventBus: bus);

        await job.Handle();

        TrackAudioAnalysis? row = ReadRow();

        Assert.NotNull(row);
        Assert.Equal(AudioAnalysisState.Ok, row.State);
    }

    [Fact]
    public async Task AFailedVerdict_PublishesACompletedEvent()
    {
        Mock<IEventBus> bus = new();

        await CreateJob(null, eventBus: bus).Handle();

        bus.Verify(
            b =>
                b.PublishAsync(
                    It.Is<TrackAudioAnalysisCompletedEvent>(e =>
                        e.TrackId == _trackId
                        && e.State == "Failed"
                        && e.LibraryIds.Count == 2
                        && e.LibraryIds.Contains(_libraryOneId)
                        && e.LibraryIds.Contains(_libraryTwoId)
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // The plugin topic is published on the Failed path too: a plugin has
        // no other way to learn a track's analysis ended in Failed, since it
        // never sees TrackAudioAnalysisCompletedEvent directly.
        bus.Verify(
            b =>
                b.PublishAsync(
                    It.Is<PluginMessageEvent>(e =>
                        e.Name == PluginTopics.MusicAnalysisCompleted
                        && e.PluginId == Ulid.Empty
                        && PayloadMatchesTheCompletedEvent(e, "Failed")
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    /// <summary>
    /// The plugin topic is a second, independent announcement of the same
    /// verdict: a plugin subscribing through <c>IPluginContext.Events</c>
    /// never sees <see cref="TrackAudioAnalysisCompletedEvent" /> directly, so
    /// the job has to publish both, with the same values, every time.
    /// </summary>
    [Fact]
    public async Task ACompletedAnalysis_PublishesBothTheHostEventAndThePluginTopic()
    {
        Mock<IEventBus> bus = new();

        await CreateJob(SampleResult(), eventBus: bus).Handle();

        bus.Verify(
            b =>
                b.PublishAsync(
                    It.Is<TrackAudioAnalysisCompletedEvent>(e =>
                        e.TrackId == _trackId
                        && e.State == "Ok"
                        && e.AnalyzerVersion == AnalyzerVersion
                        && e.LibraryIds.Count == 2
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        bus.Verify(
            b =>
                b.PublishAsync(
                    It.Is<PluginMessageEvent>(e =>
                        e.Name == PluginTopics.MusicAnalysisCompleted
                        && e.PluginId == Ulid.Empty
                        && PayloadMatchesTheCompletedEvent(e, "Ok")
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    private bool PayloadMatchesTheCompletedEvent(PluginMessageEvent published, string expectedState)
    {
        PluginMusicAnalysisCompleted? payload = published.PayloadAs<PluginMusicAnalysisCompleted>();

        return payload is not null
            && payload.TrackId == _trackId
            && payload.AnalyzerVersion == AnalyzerVersion
            && payload.State == expectedState
            && payload.LibraryIds.Count == 2
            && payload.LibraryIds.Contains(_libraryOneId.ToString())
            && payload.LibraryIds.Contains(_libraryTwoId.ToString());
    }

    /// <summary>
    /// Keeps the rendered text of every warning, so a test can assert what a
    /// log line actually says rather than only that one was written.
    /// </summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Warnings);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class RecordingLogger(List<string> warnings) : ILogger
        {
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
                if (logLevel == LogLevel.Warning)
                    warnings.Add(formatter(state, exception));
            }
        }
    }
}
