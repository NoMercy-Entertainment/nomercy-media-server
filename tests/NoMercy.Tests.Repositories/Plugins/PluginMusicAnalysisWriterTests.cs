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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// The host side of <see cref="IPluginMusicAnalysisWriter" />: every refusal
/// the writer can hand back, and that a valid write actually lands the way
/// the record described it.
/// </summary>
public class PluginMusicAnalysisWriterTests : IDisposable
{
    private const int BaseAnalyzerVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Ulid _pluginId = Ulid.NewUlid();

    // Has a current Ok base row and a "03:45" (225 000 ms) duration - the
    // track most tests write a DJ row or a stem for.
    private readonly Guid _trackId = Guid.NewGuid();

    // Exists, but never got a base analysis row: exercises "no base row".
    private readonly Guid _trackWithoutBaseId = Guid.NewGuid();

    // Has a current Ok base row, but the library never recorded a duration:
    // exercises "duration unknown, skip the ms-range check".
    private readonly Guid _trackUnknownDurationId = Guid.NewGuid();

    public PluginMusicAnalysisWriterTests()
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

        context.Tracks.AddRange(
            new Track
            {
                Id = _trackId,
                Name = "Track A",
                Duration = "03:45",
            },
            new Track
            {
                Id = _trackWithoutBaseId,
                Name = "Track B",
                Duration = "03:45",
            },
            new Track
            {
                Id = _trackUnknownDurationId,
                Name = "Track C",
                Duration = "",
            }
        );

        context.TrackAudioAnalysis.AddRange(
            new TrackAudioAnalysis
            {
                TrackId = _trackId,
                AnalyzerVersion = BaseAnalyzerVersion,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackAudioAnalysis
            {
                TrackId = _trackUnknownDurationId,
                AnalyzerVersion = BaseAnalyzerVersion,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            }
        );

        context.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Mock<IDerivedAudioStore> NewStoreMock()
    {
        Mock<IDerivedAudioStore> mock = new();
        mock.Setup(store => store.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return mock;
    }

    private PluginMusicAnalysisWriter CreateWriter(IDerivedAudioStore store)
    {
        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        return new PluginMusicAnalysisWriter(_pluginId, factory.Object, store);
    }

    private static PluginTrackDjAnalysis ValidRecord(Guid trackId) =>
        new(
            trackId,
            DjAnalyzerVersion: 3,
            BaseAnalyzerVersion: BaseAnalyzerVersion,
            DownbeatIndex: 0,
            BeatsPerBar: 4,
            PhraseLengthBars: 8,
            PhraseStartsMs: [0, 32000, 64000],
            VocalRegionsMs:
            [
                [1000, 5000],
                [9000, 15000],
            ],
            BarEnergy: [-20.5, -18.2, -14.0],
            CuePoints: [new PluginCuePoint(1000, "intro", "mixIn", 0.9)],
            Chords: [new PluginChord(0, "Am")]
        );

    private static PluginTrackStem ValidFullStem(Guid trackId, string storageKey) =>
        new(
            trackId,
            "vocals",
            PluginStemCoverage.Full,
            null,
            null,
            "opus",
            48000,
            storageKey,
            "spleeter-2stems-f16@v1"
        );

    // --- UpsertDjAnalysisAsync: refusals, checked in the brief's order -----

    [Fact]
    public async Task UpsertDjAnalysisAsync_RefusesAnUnknownTrack()
    {
        Guid unknownTrackId = Guid.NewGuid();

        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .UpsertDjAnalysisAsync(ValidRecord(unknownTrackId));

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("does not exist");
    }

    [Fact]
    public async Task UpsertDjAnalysisAsync_RefusesWhenThereIsNoBaseAnalysisAtThatVersion()
    {
        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .UpsertDjAnalysisAsync(ValidRecord(_trackWithoutBaseId));

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("no base analysis at version");
    }

    [Fact]
    public async Task UpsertDjAnalysisAsync_RefusesBeatsPerBarBelowOne()
    {
        PluginTrackDjAnalysis record = ValidRecord(_trackId) with { BeatsPerBar = 0 };

        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .UpsertDjAnalysisAsync(record);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("beats_per_bar");
    }

    [Fact]
    public async Task UpsertDjAnalysisAsync_RefusesADownbeatIndexOutsideTheBeatGrid()
    {
        PluginTrackDjAnalysis record = ValidRecord(_trackId) with { DownbeatIndex = 4 };

        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .UpsertDjAnalysisAsync(record);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("downbeat_index");
    }

    [Fact]
    public async Task UpsertDjAnalysisAsync_RefusesAnMsValueOutsideTheTrack()
    {
        PluginTrackDjAnalysis record = ValidRecord(_trackId) with
        {
            PhraseStartsMs = [0, 32000, 500000],
        };

        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .UpsertDjAnalysisAsync(record);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("phrase_starts_ms value 500000 lies outside the track");
    }

    /// <summary>
    /// When the library never recorded a duration, the ms-range check cannot
    /// run at all - the value that would fail it must not be refused, only
    /// silently skipped.
    /// </summary>
    [Fact]
    public async Task UpsertDjAnalysisAsync_SkipsTheMsRangeCheckWhenTheDurationIsUnknown()
    {
        PluginTrackDjAnalysis record = ValidRecord(_trackUnknownDurationId) with
        {
            PhraseStartsMs = [0, 32000, 500000],
        };

        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .UpsertDjAnalysisAsync(record);

        result.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertDjAnalysisAsync_RefusesNonAscendingPhraseStarts()
    {
        PluginTrackDjAnalysis record = ValidRecord(_trackId) with
        {
            PhraseStartsMs = [0, 32000, 16000],
        };

        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .UpsertDjAnalysisAsync(record);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("phrase_starts_ms must be ascending");
    }

    [Fact]
    public async Task UpsertDjAnalysisAsync_RefusesAnOversizedJsonColumn()
    {
        List<double> hugeBarEnergy = Enumerable.Repeat(-20.5, 20000).ToList();
        PluginTrackDjAnalysis record = ValidRecord(_trackId) with { BarEnergy = hugeBarEnergy };

        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .UpsertDjAnalysisAsync(record);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("bar_energy exceeds 64 kB");
    }

    // --- UpsertDjAnalysisAsync: happy paths ---------------------------------

    [Fact]
    public async Task AValidRecord_IsStoredWithTheProducerStamped()
    {
        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .UpsertDjAnalysisAsync(ValidRecord(_trackId));

        result.Ok.Should().BeTrue();

        using MediaContext context = new(_options);
        TrackDjAnalysis row = context.TrackDjAnalysis.Single(a => a.TrackId == _trackId);

        row.ProducerPluginId.Should().Be(_pluginId);
        row.State.Should().Be(AudioAnalysisState.Ok);
        row.FailureReason.Should().BeNull();
        row.DjAnalyzerVersion.Should().Be(3);
        row.PhraseStartsMs.Should().Be("[0,32000,64000]");
        row.CuePoints.Should().Contain("\"ms\":1000").And.Contain("\"direction\":\"mixIn\"");
        row.Chords.Should().Contain("\"chord\":\"Am\"");
    }

    [Fact]
    public async Task UpsertTwice_ReplacesTheRow()
    {
        PluginMusicAnalysisWriter writer = CreateWriter(NewStoreMock().Object);

        await writer.UpsertDjAnalysisAsync(ValidRecord(_trackId));

        PluginTrackDjAnalysis second = ValidRecord(_trackId) with
        {
            DjAnalyzerVersion = 4,
            PhraseStartsMs = [0, 16000],
        };
        PluginWriteResult result = await writer.UpsertDjAnalysisAsync(second);

        result.Ok.Should().BeTrue();

        using MediaContext context = new(_options);
        context.TrackDjAnalysis.Count(a => a.TrackId == _trackId).Should().Be(1);

        TrackDjAnalysis row = context.TrackDjAnalysis.Single(a => a.TrackId == _trackId);
        row.DjAnalyzerVersion.Should().Be(4);
        row.PhraseStartsMs.Should().Be("[0,16000]");
    }

    [Fact]
    public async Task EmptyLists_AreStoredAsEmptyJsonArraysNeverNull()
    {
        PluginTrackDjAnalysis record = ValidRecord(_trackId) with
        {
            PhraseStartsMs = [],
            VocalRegionsMs = [],
            BarEnergy = [],
            CuePoints = [],
            Chords = [],
        };

        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .UpsertDjAnalysisAsync(record);

        result.Ok.Should().BeTrue();

        using MediaContext context = new(_options);
        TrackDjAnalysis row = context.TrackDjAnalysis.Single(a => a.TrackId == _trackId);

        row.PhraseStartsMs.Should().Be("[]");
        row.VocalRegionsMs.Should().Be("[]");
        row.BarEnergy.Should().Be("[]");
        row.CuePoints.Should().Be("[]");
        row.Chords.Should().Be("[]");
    }

    // --- RegisterStemAsync: refusals ---------------------------------------

    [Fact]
    public async Task RegisterStemAsync_RefusesAnUnknownTrack()
    {
        Mock<IDerivedAudioStore> store = NewStoreMock();
        store.Setup(s => s.ExistsAsync("key-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        PluginWriteResult result = await CreateWriter(store.Object)
            .RegisterStemAsync(ValidFullStem(Guid.NewGuid(), "key-1"));

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("does not exist");
    }

    [Fact]
    public async Task RegisterStem_RefusesAnUnknownKey()
    {
        // NewStoreMock's ExistsAsync answers false for every key by default.
        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .RegisterStemAsync(ValidFullStem(_trackId, "missing-key"));

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("is not in the derived store");
    }

    [Fact]
    public async Task RegisterStemAsync_RefusesFullCoverageWithAWindow()
    {
        Mock<IDerivedAudioStore> store = NewStoreMock();
        store.Setup(s => s.ExistsAsync("key-2", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        PluginTrackStem stem = ValidFullStem(_trackId, "key-2") with
        {
            WindowStartMs = 0,
            WindowEndMs = 1000,
        };

        PluginWriteResult result = await CreateWriter(store.Object).RegisterStemAsync(stem);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("must not specify a window");
    }

    [Fact]
    public async Task RegisterStemAsync_RefusesWindowedCoverageWithoutAWindow()
    {
        Mock<IDerivedAudioStore> store = NewStoreMock();
        store.Setup(s => s.ExistsAsync("key-3", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        PluginTrackStem stem = new(
            _trackId,
            "vocals",
            PluginStemCoverage.MixIn,
            null,
            null,
            "opus",
            48000,
            "key-3",
            "spleeter-2stems-f16@v1"
        );

        PluginWriteResult result = await CreateWriter(store.Object).RegisterStemAsync(stem);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("must specify both window_start_ms and window_end_ms");
    }

    [Fact]
    public async Task RegisterStemAsync_RefusesAWindowThatDoesNotStartBeforeItEnds()
    {
        Mock<IDerivedAudioStore> store = NewStoreMock();
        store.Setup(s => s.ExistsAsync("key-4", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        PluginTrackStem stem = new(
            _trackId,
            "vocals",
            PluginStemCoverage.MixIn,
            1000,
            1000,
            "opus",
            48000,
            "key-4",
            "spleeter-2stems-f16@v1"
        );

        PluginWriteResult result = await CreateWriter(store.Object).RegisterStemAsync(stem);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("window_start_ms must be less than window_end_ms");
    }

    // --- RegisterStemAsync: happy path --------------------------------------

    [Fact]
    public async Task RegisterStem_ReplacesTheSameStem()
    {
        Mock<IDerivedAudioStore> store = NewStoreMock();
        store
            .Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        PluginMusicAnalysisWriter writer = CreateWriter(store.Object);

        await writer.RegisterStemAsync(ValidFullStem(_trackId, "key-a"));
        PluginWriteResult result = await writer.RegisterStemAsync(ValidFullStem(_trackId, "key-b"));

        result.Ok.Should().BeTrue();

        using MediaContext context = new(_options);
        context.TrackStems.Count(s => s.TrackId == _trackId).Should().Be(1);

        TrackStem row = context.TrackStems.Single(s => s.TrackId == _trackId);
        row.StorageKey.Should().Be("key-b");
    }

    // --- MarkFailedAsync -----------------------------------------------------

    [Fact]
    public async Task MarkFailedAsync_RefusesAnUnknownTrack()
    {
        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .MarkFailedAsync(Guid.NewGuid(), 3, BaseAnalyzerVersion, "no audio stream found");

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("does not exist");
    }

    [Fact]
    public async Task MarkFailed_WritesAFailedRow()
    {
        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .MarkFailedAsync(_trackId, 3, BaseAnalyzerVersion, "no audio stream found");

        result.Ok.Should().BeTrue();

        using MediaContext context = new(_options);
        TrackDjAnalysis row = context.TrackDjAnalysis.Single(a => a.TrackId == _trackId);

        row.State.Should().Be(AudioAnalysisState.Failed);
        row.FailureReason.Should().Be("no audio stream found");
        row.DjAnalyzerVersion.Should().Be(3);
        row.BaseAnalyzerVersion.Should().Be(BaseAnalyzerVersion);
        row.ProducerPluginId.Should().Be(_pluginId);
    }

    [Fact]
    public async Task MarkFailedAsync_TruncatesAnOverlongReasonTo1024Characters()
    {
        string longReason = new('x', 2000);

        PluginWriteResult result = await CreateWriter(NewStoreMock().Object)
            .MarkFailedAsync(_trackId, 3, BaseAnalyzerVersion, longReason);

        result.Ok.Should().BeTrue();

        using MediaContext context = new(_options);
        TrackDjAnalysis row = context.TrackDjAnalysis.Single(a => a.TrackId == _trackId);

        row.FailureReason.Should().NotBeNull();
        row.FailureReason!.Length.Should().Be(1024);
    }

    /// <summary>
    /// A Failed row must never go on carrying a previous Ok row's
    /// measurements - a caller reading a stale <c>DjAnalyzerVersion</c>
    /// alongside <c>State = Failed</c> has to see nothing behind it, not the
    /// last successful run's numbers.
    /// </summary>
    [Fact]
    public async Task MarkFailed_AfterAnOkRow_ClearsTheMeasurements()
    {
        PluginMusicAnalysisWriter writer = CreateWriter(NewStoreMock().Object);

        await writer.UpsertDjAnalysisAsync(ValidRecord(_trackId));

        PluginWriteResult result = await writer.MarkFailedAsync(
            _trackId,
            4,
            BaseAnalyzerVersion,
            "vocal detector produced no regions"
        );

        result.Ok.Should().BeTrue();

        using MediaContext context = new(_options);
        TrackDjAnalysis row = context.TrackDjAnalysis.Single(a => a.TrackId == _trackId);

        row.State.Should().Be(AudioAnalysisState.Failed);
        row.DjAnalyzerVersion.Should().Be(4);
        row.DownbeatIndex.Should().BeNull();
        row.BeatsPerBar.Should().Be(4);
        row.PhraseLengthBars.Should().Be(8);
        row.PhraseStartsMs.Should().BeNull();
        row.VocalRegionsMs.Should().BeNull();
        row.BarEnergy.Should().BeNull();
        row.CuePoints.Should().BeNull();
        row.Chords.Should().BeNull();
    }

    // --- DeleteDjAnalysisAsync -----------------------------------------------

    [Fact]
    public async Task Delete_RemovesOnlyTheDjRow()
    {
        Mock<IDerivedAudioStore> store = NewStoreMock();
        store
            .Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        PluginMusicAnalysisWriter writer = CreateWriter(store.Object);
        await writer.UpsertDjAnalysisAsync(ValidRecord(_trackId));
        await writer.RegisterStemAsync(ValidFullStem(_trackId, "key-delete"));

        await writer.DeleteDjAnalysisAsync(_trackId);

        using MediaContext context = new(_options);
        context.TrackDjAnalysis.Any(a => a.TrackId == _trackId).Should().BeFalse();
        context.TrackStems.Any(s => s.TrackId == _trackId).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteDjAnalysisAsync_IsANoOpWhenNoRowExists()
    {
        Func<Task> act = async () =>
            await CreateWriter(NewStoreMock().Object).DeleteDjAnalysisAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    // --- PluginMusicAnalysisWriterFactory -------------------------------------

    /// <summary>
    /// The unknown-duration log line is worthless if it never reaches a real
    /// sink: the factory has to hand every writer it builds the logger it was
    /// given, not leave it on the writer's <c>NullLogger</c> fallback.
    /// </summary>
    [Fact]
    public async Task CreateFor_GivesTheWriterTheFactorysLogger()
    {
        Mock<ILogger<PluginMusicAnalysisWriter>> loggerMock = new();

        Mock<IDbContextFactory<MediaContext>> contextFactoryMock = new();
        contextFactoryMock
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        PluginMusicAnalysisWriterFactory factory = new(
            contextFactoryMock.Object,
            NewStoreMock().Object,
            loggerMock.Object
        );

        IPluginMusicAnalysisWriter writer = factory.CreateFor(_pluginId);
        await writer.UpsertDjAnalysisAsync(ValidRecord(_trackUnknownDurationId));

        loggerMock.Verify(
            logger =>
                logger.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>(
                        (state, _) => state.ToString()!.Contains("duration is unknown")
                    ),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }
}
