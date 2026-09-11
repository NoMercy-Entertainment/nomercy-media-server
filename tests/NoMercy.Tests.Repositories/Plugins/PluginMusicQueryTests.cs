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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

public class PluginMusicQueryTests : IDisposable
{
    // The DJ analyzer version the "needs analysis" tests ask for. Rows built
    // against an older or newer value exercise the version-mismatch branches.
    private const int CurrentDjVersion = 3;
    private const string StemProducerVersion = "spleeter-2stems-f16@v1.0.41";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Ulid _libraryId = Ulid.NewUlid();

    // A second library, kept separate from _libraryId, so seeding the DJ and
    // stem fixtures below never changes what GetTracksAsync sees for
    // _libraryId — those counts are asserted exactly by the existing tests.
    private readonly Ulid _djLibraryId = Ulid.NewUlid();

    private readonly Guid _analyzedTrackId = Guid.NewGuid();
    private readonly Guid _unanalyzedTrackId = Guid.NewGuid();
    private readonly Guid _failedTrackId = Guid.NewGuid();

    private readonly Guid _djOkTrackId = Guid.NewGuid();
    private readonly Guid _djFailedTrackId = Guid.NewGuid();

    private readonly Guid _needsNoRowTrackId = Guid.NewGuid();
    private readonly Guid _needsCurrentTrackId = Guid.NewGuid();
    private readonly Guid _needsOlderVersionTrackId = Guid.NewGuid();
    private readonly Guid _needsStaleBaseTrackId = Guid.NewGuid();
    private readonly Guid _needsBaseFailedTrackId = Guid.NewGuid();
    private readonly Guid _needsPendingTrackId = Guid.NewGuid();
    private readonly Guid _needsFailedCurrentTrackId = Guid.NewGuid();

    private readonly Guid _stemsWindowsMissingTrackId = Guid.NewGuid();
    private readonly Guid _stemsFullMissingFromWindowsTrackId = Guid.NewGuid();
    private readonly Guid _stemsFullPresentTrackId = Guid.NewGuid();

    public PluginMusicQueryTests()
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

        context.Libraries.AddRange(
            new Library
            {
                Id = _libraryId,
                Title = "Music",
                Type = "music",
                AnalyzeAudio = true,
            },
            new Library
            {
                Id = _djLibraryId,
                Title = "Dj Music",
                Type = "music",
                AnalyzeAudio = true,
            }
        );

        context.Tracks.AddRange(
            new Track
            {
                Id = _analyzedTrackId,
                Name = "Analyzed",
                TrackNumber = 3,
                DiscNumber = 1,
                Duration = "03:45",
            },
            new Track { Id = _unanalyzedTrackId, Name = "Unanalyzed" },
            new Track
            {
                Id = _failedTrackId,
                Name = "Failed",
                Duration = "01:02:03",
            }
        );

        context.LibraryTrack.AddRange(
            new LibraryTrack { LibraryId = _libraryId, TrackId = _analyzedTrackId },
            new LibraryTrack { LibraryId = _libraryId, TrackId = _unanalyzedTrackId },
            new LibraryTrack { LibraryId = _libraryId, TrackId = _failedTrackId }
        );

        context.TrackAudioAnalysis.AddRange(
            new TrackAudioAnalysis
            {
                TrackId = _analyzedTrackId,
                AnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                Bpm = 128.0,
                KeyName = "Am",
                KeyCamelot = "8A",
                KeyConfidence = 0.82,
                IntegratedLufs = -9.4,
                TruePeakDb = -0.3,
                Energy = 0.71,
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackAudioAnalysis
            {
                TrackId = _failedTrackId,
                AnalyzerVersion = 1,
                State = AudioAnalysisState.Failed,
                FailureReason = "analysis produced no measurements",
                AnalyzedAt = DateTime.UtcNow,
            }
        );

        SeedDjAnalysisFixture(context);
        SeedNeedsDjAnalysisFixture(context);
        SeedStemsFixture(context);

        context.SaveChanges();
    }

    /// <summary>Tracks for <c>GetDjAnalysisAsync</c>: one Ok row with every JSON
    /// column populated, one Failed row that must never come back.</summary>
    private void SeedDjAnalysisFixture(MediaContext context)
    {
        context.Tracks.AddRange(
            new Track { Id = _djOkTrackId, Name = "Dj Ok" },
            new Track { Id = _djFailedTrackId, Name = "Dj Failed" }
        );

        context.TrackDjAnalysis.AddRange(
            new TrackDjAnalysis
            {
                TrackId = _djOkTrackId,
                ProducerPluginId = Ulid.NewUlid(),
                DjAnalyzerVersion = CurrentDjVersion,
                BaseAnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                DownbeatIndex = 0,
                BeatsPerBar = 4,
                PhraseLengthBars = 8,
                PhraseStartsMs = "[0,32000,64000]",
                VocalRegionsMs = "[[1000,5000],[9000,15000]]",
                BarEnergy = "[-20.5,-18.2,-14.0]",
                CuePoints = """[{"ms":1000,"type":"intro","direction":"mixIn","score":0.9}]""",
                Chords = """[{"ms":0,"chord":"Am"}]""",
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackDjAnalysis
            {
                TrackId = _djFailedTrackId,
                ProducerPluginId = Ulid.NewUlid(),
                DjAnalyzerVersion = CurrentDjVersion,
                BaseAnalyzerVersion = 1,
                State = AudioAnalysisState.Failed,
                FailureReason = "vocal detector produced no regions",
                AnalyzedAt = DateTime.UtcNow,
            }
        );
    }

    /// <summary>
    /// Tracks for <c>GetTracksNeedingDjAnalysisAsync</c>, one per branch of the
    /// "does this track already have a current DJ row" rule.
    /// </summary>
    private void SeedNeedsDjAnalysisFixture(MediaContext context)
    {
        context.Tracks.AddRange(
            new Track { Id = _needsNoRowTrackId, Name = "Needs: no row" },
            new Track { Id = _needsCurrentTrackId, Name = "Needs: current" },
            new Track { Id = _needsOlderVersionTrackId, Name = "Needs: older version" },
            new Track { Id = _needsStaleBaseTrackId, Name = "Needs: stale base" },
            new Track { Id = _needsBaseFailedTrackId, Name = "Needs: base failed" },
            new Track { Id = _needsPendingTrackId, Name = "Needs: pending" },
            new Track { Id = _needsFailedCurrentTrackId, Name = "Needs: failed current" }
        );

        context.LibraryTrack.AddRange(
            new LibraryTrack { LibraryId = _djLibraryId, TrackId = _needsNoRowTrackId },
            new LibraryTrack { LibraryId = _djLibraryId, TrackId = _needsCurrentTrackId },
            new LibraryTrack { LibraryId = _djLibraryId, TrackId = _needsOlderVersionTrackId },
            new LibraryTrack { LibraryId = _djLibraryId, TrackId = _needsStaleBaseTrackId },
            new LibraryTrack { LibraryId = _djLibraryId, TrackId = _needsBaseFailedTrackId },
            new LibraryTrack { LibraryId = _djLibraryId, TrackId = _needsPendingTrackId },
            new LibraryTrack { LibraryId = _djLibraryId, TrackId = _needsFailedCurrentTrackId }
        );

        context.TrackAudioAnalysis.AddRange(
            new TrackAudioAnalysis
            {
                TrackId = _needsNoRowTrackId,
                AnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackAudioAnalysis
            {
                TrackId = _needsCurrentTrackId,
                AnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackAudioAnalysis
            {
                TrackId = _needsOlderVersionTrackId,
                AnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackAudioAnalysis
            {
                TrackId = _needsStaleBaseTrackId,
                AnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackAudioAnalysis
            {
                TrackId = _needsBaseFailedTrackId,
                AnalyzerVersion = 1,
                State = AudioAnalysisState.Failed,
                FailureReason = "unreadable file",
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackAudioAnalysis
            {
                TrackId = _needsPendingTrackId,
                AnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackAudioAnalysis
            {
                TrackId = _needsFailedCurrentTrackId,
                AnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            }
        );

        context.TrackDjAnalysis.AddRange(
            // _needsCurrentTrackId: version and base both match, Ok — already
            // has a current DJ row, so it must be the one track excluded.
            new TrackDjAnalysis
            {
                TrackId = _needsCurrentTrackId,
                ProducerPluginId = Ulid.NewUlid(),
                DjAnalyzerVersion = CurrentDjVersion,
                BaseAnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            // Older DJ analyzer than requested: stale, needs redoing.
            new TrackDjAnalysis
            {
                TrackId = _needsOlderVersionTrackId,
                ProducerPluginId = Ulid.NewUlid(),
                DjAnalyzerVersion = CurrentDjVersion - 1,
                BaseAnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            // Current DJ analyzer, but computed from a base analysis that has
            // since moved on to a newer AnalyzerVersion than this row records.
            new TrackDjAnalysis
            {
                TrackId = _needsStaleBaseTrackId,
                ProducerPluginId = Ulid.NewUlid(),
                DjAnalyzerVersion = CurrentDjVersion,
                BaseAnalyzerVersion = 99,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            // A run that started and never finished.
            new TrackDjAnalysis
            {
                TrackId = _needsPendingTrackId,
                ProducerPluginId = Ulid.NewUlid(),
                DjAnalyzerVersion = CurrentDjVersion,
                BaseAnalyzerVersion = 1,
                State = AudioAnalysisState.Pending,
                AnalyzedAt = DateTime.UtcNow,
            },
            // Version and base both match, but the run itself failed. This is
            // the terminal case: retried only when the DJ analyzer version
            // changes, never just because the sweep runs again — a bare
            // "!= Pending" -> "== Ok" simplification here would silently
            // reopen endless re-analysis of tracks that will never succeed.
            new TrackDjAnalysis
            {
                TrackId = _needsFailedCurrentTrackId,
                ProducerPluginId = Ulid.NewUlid(),
                DjAnalyzerVersion = CurrentDjVersion,
                BaseAnalyzerVersion = 1,
                State = AudioAnalysisState.Failed,
                FailureReason = "vocal detector produced no regions",
                AnalyzedAt = DateTime.UtcNow,
            }
        );
    }

    /// <summary>Tracks for <c>GetTracksMissingStemsAsync</c> and <c>GetStemsAsync</c>.</summary>
    private void SeedStemsFixture(MediaContext context)
    {
        context.Tracks.AddRange(
            new Track { Id = _stemsWindowsMissingTrackId, Name = "Stems: windows missing" },
            new Track
            {
                Id = _stemsFullMissingFromWindowsTrackId,
                Name = "Stems: full missing from windows",
            },
            new Track { Id = _stemsFullPresentTrackId, Name = "Stems: full present" }
        );

        context.LibraryTrack.AddRange(
            new LibraryTrack { LibraryId = _djLibraryId, TrackId = _stemsWindowsMissingTrackId },
            new LibraryTrack
            {
                LibraryId = _djLibraryId,
                TrackId = _stemsFullMissingFromWindowsTrackId,
            },
            new LibraryTrack { LibraryId = _djLibraryId, TrackId = _stemsFullPresentTrackId }
        );

        // Every one of these tracks has an Ok DJ row: GetTracksMissingStemsAsync
        // only ever asks about stems for a track the DJ analyzer already ran on.
        context.TrackDjAnalysis.AddRange(
            new TrackDjAnalysis
            {
                TrackId = _stemsWindowsMissingTrackId,
                ProducerPluginId = Ulid.NewUlid(),
                DjAnalyzerVersion = CurrentDjVersion,
                BaseAnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackDjAnalysis
            {
                TrackId = _stemsFullMissingFromWindowsTrackId,
                ProducerPluginId = Ulid.NewUlid(),
                DjAnalyzerVersion = CurrentDjVersion,
                BaseAnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            },
            new TrackDjAnalysis
            {
                TrackId = _stemsFullPresentTrackId,
                ProducerPluginId = Ulid.NewUlid(),
                DjAnalyzerVersion = CurrentDjVersion,
                BaseAnalyzerVersion = 1,
                State = AudioAnalysisState.Ok,
                AnalyzedAt = DateTime.UtcNow,
            }
        );

        string mixInKey = NewStorageKey();
        string mixOutKey = NewStorageKey();
        string windowsMixInKey = NewStorageKey();
        string fullVocalsKey = NewStorageKey();
        string fullAccompanimentKey = NewStorageKey();

        context.DerivedAudio.AddRange(
            NewDerivedAudioRow(windowsMixInKey),
            NewDerivedAudioRow(mixInKey),
            NewDerivedAudioRow(mixOutKey),
            NewDerivedAudioRow(fullVocalsKey),
            NewDerivedAudioRow(fullAccompanimentKey)
        );

        context.TrackStems.AddRange(
            // Windows policy needs MixIn + MixOut; this track has only MixIn.
            new TrackStem
            {
                Id = Ulid.NewUlid(),
                TrackId = _stemsWindowsMissingTrackId,
                Kind = "vocals",
                Coverage = StemCoverage.MixIn,
                WindowStartMs = 0,
                WindowEndMs = 45000,
                Format = "opus",
                SampleRate = 48000,
                StorageKey = windowsMixInKey,
                ProducerVersion = StemProducerVersion,
                CreatedAt = DateTime.UtcNow,
            },
            // Full policy needs a Full stem; this track has both windows but no
            // Full row, so Full policy still calls it missing.
            new TrackStem
            {
                Id = Ulid.NewUlid(),
                TrackId = _stemsFullMissingFromWindowsTrackId,
                Kind = "vocals",
                Coverage = StemCoverage.MixIn,
                WindowStartMs = 0,
                WindowEndMs = 45000,
                Format = "opus",
                SampleRate = 48000,
                StorageKey = mixInKey,
                ProducerVersion = StemProducerVersion,
                CreatedAt = DateTime.UtcNow,
            },
            new TrackStem
            {
                Id = Ulid.NewUlid(),
                TrackId = _stemsFullMissingFromWindowsTrackId,
                Kind = "vocals",
                Coverage = StemCoverage.MixOut,
                WindowStartMs = 135000,
                WindowEndMs = 180000,
                Format = "opus",
                SampleRate = 48000,
                StorageKey = mixOutKey,
                ProducerVersion = StemProducerVersion,
                CreatedAt = DateTime.UtcNow,
            },
            // Full policy is satisfied: a Full vocals stem exists. A second,
            // different-kind stem proves GetStemsAsync returns every kind.
            new TrackStem
            {
                Id = Ulid.NewUlid(),
                TrackId = _stemsFullPresentTrackId,
                Kind = "vocals",
                Coverage = StemCoverage.Full,
                Format = "opus",
                SampleRate = 48000,
                StorageKey = fullVocalsKey,
                ProducerVersion = StemProducerVersion,
                CreatedAt = DateTime.UtcNow,
            },
            new TrackStem
            {
                Id = Ulid.NewUlid(),
                TrackId = _stemsFullPresentTrackId,
                Kind = "accompaniment",
                Coverage = StemCoverage.Full,
                Format = "opus",
                SampleRate = 48000,
                StorageKey = fullAccompanimentKey,
                ProducerVersion = StemProducerVersion,
                CreatedAt = DateTime.UtcNow,
            }
        );
    }

    private static DerivedAudio NewDerivedAudioRow(string key) =>
        new()
        {
            Key = key,
            ContentType = "audio/opus",
            Bytes = 10,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
        };

    private static string NewStorageKey() =>
        Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private PluginMusicQuery CreateQuery()
    {
        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        return new PluginMusicQuery(factory.Object, NullLogger<PluginMusicQuery>.Instance);
    }

    [Fact]
    public async Task GetTracksAsync_ReturnsTheLibrarysTracks()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(_libraryId.ToString());

        tracks.Should().HaveCount(3);
        tracks.Select(track => track.Id).Should().Contain(_analyzedTrackId);
    }

    /// <summary>
    /// The library stores a duration as the "mm:ss" string ffprobe printed. A
    /// plugin planning a transition needs seconds, so the conversion is done
    /// here, once, rather than in every plugin.
    /// </summary>
    [Fact]
    public async Task GetTracksAsync_ConvertsTheStoredDurationToSeconds()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(_libraryId.ToString());

        tracks.Single(track => track.Id == _analyzedTrackId).DurationSeconds.Should().Be(225.0);
    }

    /// <summary>
    /// The leading "00:" is stripped when a track is stored, so anything of an
    /// hour or more keeps three parts and a shorter track has two.
    /// </summary>
    [Fact]
    public async Task GetTracksAsync_ReadsAnHourLongDurationAsHoursMinutesSeconds()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(_libraryId.ToString());

        tracks.Single(track => track.Id == _failedTrackId).DurationSeconds.Should().Be(3723.0);
    }

    [Fact]
    public async Task GetTracksAsync_LeavesDurationNullWhenTheLibraryHasNone()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(_libraryId.ToString());

        tracks.Single(track => track.Id == _unanalyzedTrackId).DurationSeconds.Should().BeNull();
    }

    [Fact]
    public async Task GetTracksAsync_ReturnsNothingForAnotherLibrary()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(Ulid.NewUlid().ToString());

        tracks.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTracksAsync_Pages()
    {
        PluginMusicQuery query = CreateQuery();

        IReadOnlyList<PluginTrack> first = await query.GetTracksAsync(
            _libraryId.ToString(),
            skip: 0,
            take: 2
        );
        IReadOnlyList<PluginTrack> second = await query.GetTracksAsync(
            _libraryId.ToString(),
            skip: 2,
            take: 2
        );

        first.Should().HaveCount(2);
        second.Should().HaveCount(1);
        first.Select(track => track.Id).Should().NotIntersectWith(second.Select(track => track.Id));
    }

    [Fact]
    public async Task GetAnalysisAsync_ReturnsTheMeasurements()
    {
        IReadOnlyList<PluginTrackAudioAnalysis> analysis = await CreateQuery()
            .GetAnalysisAsync([_analyzedTrackId]);

        analysis.Should().ContainSingle();
        analysis[0].Bpm.Should().Be(128.0);
        analysis[0].KeyCamelot.Should().Be("8A");
        analysis[0].IntegratedLufs.Should().Be(-9.4);
        analysis[0].AnalyzerVersion.Should().Be(1);
    }

    /// <summary>
    /// A failed row is an absence to a plugin, not a set of null readings it has
    /// to learn to distinguish from a real measurement of zero.
    /// </summary>
    [Fact]
    public async Task GetAnalysisAsync_OmitsRowsThatFailed()
    {
        IReadOnlyList<PluginTrackAudioAnalysis> analysis = await CreateQuery()
            .GetAnalysisAsync([_failedTrackId]);

        analysis.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAnalysisAsync_OmitsTracksWithNoAnalysisAtAll()
    {
        IReadOnlyList<PluginTrackAudioAnalysis> analysis = await CreateQuery()
            .GetAnalysisAsync([_unanalyzedTrackId]);

        analysis.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAnalysisAsync_ReturnsOnlyTheAnalyzedOnesFromAMixedRequest()
    {
        IReadOnlyList<PluginTrackAudioAnalysis> analysis = await CreateQuery()
            .GetAnalysisAsync([_analyzedTrackId, _unanalyzedTrackId, _failedTrackId]);

        analysis.Should().ContainSingle();
        analysis[0].TrackId.Should().Be(_analyzedTrackId);
    }

    [Fact]
    public async Task GetAnalysisAsync_ReturnsNothingForAnEmptyRequest()
    {
        IReadOnlyList<PluginTrackAudioAnalysis> analysis = await CreateQuery().GetAnalysisAsync([]);

        analysis.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDjAnalysisAsync_ReturnsOkRowsWithTypedLists()
    {
        IReadOnlyList<PluginTrackDjAnalysis> analysis = await CreateQuery()
            .GetDjAnalysisAsync([_djOkTrackId, _djFailedTrackId, _unanalyzedTrackId]);

        // Neither the Failed row nor the track with no DJ row at all comes
        // back, even though both were asked for.
        analysis.Should().ContainSingle();
        PluginTrackDjAnalysis row = analysis[0];

        row.TrackId.Should().Be(_djOkTrackId);
        row.DjAnalyzerVersion.Should().Be(CurrentDjVersion);
        row.BaseAnalyzerVersion.Should().Be(1);
        row.DownbeatIndex.Should().Be(0);
        row.BeatsPerBar.Should().Be(4);
        row.PhraseLengthBars.Should().Be(8);
        row.PhraseStartsMs.Should().Equal(0, 32000, 64000);
        row.VocalRegionsMs.Should().BeEquivalentTo([new[] { 1000, 5000 }, new[] { 9000, 15000 }]);
        row.BarEnergy.Should().Equal(-20.5, -18.2, -14.0);

        row.CuePoints.Should().ContainSingle();
        row.CuePoints[0].Ms.Should().Be(1000);
        row.CuePoints[0].Type.Should().Be("intro");
        row.CuePoints[0].Direction.Should().Be("mixIn");
        row.CuePoints[0].Score.Should().Be(0.9);

        row.Chords.Should().ContainSingle();
        row.Chords[0].Ms.Should().Be(0);
        row.Chords[0].Chord.Should().Be("Am");
    }

    [Fact]
    public async Task GetStemsAsync_ReturnsStemsOfTheGivenTracks()
    {
        IReadOnlyList<PluginTrackStem> stems = await CreateQuery()
            .GetStemsAsync([_stemsFullPresentTrackId]);

        stems.Should().HaveCount(2);
        stems.Should().OnlyContain(stem => stem.TrackId == _stemsFullPresentTrackId);
        stems.Should().OnlyContain(stem => stem.Coverage == PluginStemCoverage.Full);
        stems.Select(stem => stem.Kind).Should().BeEquivalentTo(["vocals", "accompaniment"]);
    }

    [Fact]
    public async Task GetTracksNeedingDjAnalysisAsync_ReturnsTracksWithABaseVerdictAndNoCurrentDjRow()
    {
        IReadOnlyList<Guid> needing = await CreateQuery()
            .GetTracksNeedingDjAnalysisAsync(_djLibraryId.ToString(), CurrentDjVersion, take: 1000);

        needing
            .Should()
            .Contain([
                _needsNoRowTrackId,
                _needsOlderVersionTrackId,
                _needsStaleBaseTrackId,
                _needsPendingTrackId,
            ]);
        needing.Should().NotContain(_needsCurrentTrackId);
        needing.Should().NotContain(_needsBaseFailedTrackId);
        // A Failed DJ row at the current DjAnalyzerVersion and a matching
        // BaseAnalyzerVersion is a terminal verdict, not stale work: it must
        // not be picked up again just because it isn't Ok.
        needing.Should().NotContain(_needsFailedCurrentTrackId);
    }

    [Fact]
    public async Task GetTracksNeedingDjAnalysisAsync_BadLibraryIdReturnsEmpty()
    {
        IReadOnlyList<Guid> needing = await CreateQuery()
            .GetTracksNeedingDjAnalysisAsync("not-a-valid-ulid", CurrentDjVersion);

        needing.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTracksMissingStemsAsync_HonoursThePolicy()
    {
        PluginMusicQuery query = CreateQuery();

        IReadOnlyList<Guid> windowsMissing = await query.GetTracksMissingStemsAsync(
            _djLibraryId.ToString(),
            StemProducerVersion,
            PluginStemPolicy.Windows,
            take: 1000
        );
        windowsMissing.Should().Contain(_stemsWindowsMissingTrackId);
        // A Full stem satisfies every required window by itself: a track
        // with only a Full stem is not missing anything under Windows either.
        windowsMissing.Should().NotContain(_stemsFullPresentTrackId);

        IReadOnlyList<Guid> fullMissing = await query.GetTracksMissingStemsAsync(
            _djLibraryId.ToString(),
            StemProducerVersion,
            PluginStemPolicy.Full,
            take: 1000
        );
        fullMissing.Should().Contain(_stemsFullMissingFromWindowsTrackId);
        fullMissing.Should().NotContain(_stemsFullPresentTrackId);

        IReadOnlyList<Guid> onDemand = await query.GetTracksMissingStemsAsync(
            _djLibraryId.ToString(),
            StemProducerVersion,
            PluginStemPolicy.OnDemand,
            take: 1000
        );
        onDemand.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTracksMissingStemsAsync_BadLibraryIdReturnsEmpty()
    {
        IReadOnlyList<Guid> missing = await CreateQuery()
            .GetTracksMissingStemsAsync(
                "not-a-valid-ulid",
                StemProducerVersion,
                PluginStemPolicy.Full
            );

        missing.Should().BeEmpty();
    }

    /// <summary>
    /// <paramref name="take" /> is clamped to at least 1 the same way
    /// <see cref="PluginMusicQuery.GetTracksAsync" /> clamps it, so a caller
    /// passing 0 still gets one row rather than an empty page.
    /// </summary>
    [Fact]
    public async Task Take_IsClamped_LikeGetTracksAsync()
    {
        IReadOnlyList<Guid> clamped = await CreateQuery()
            .GetTracksNeedingDjAnalysisAsync(
                _djLibraryId.ToString(),
                CurrentDjVersion,
                skip: 0,
                take: 0
            );

        clamped.Should().HaveCount(1);
    }

    /// <summary>Same clamp, the other library-scoped "needs" method.</summary>
    [Fact]
    public async Task Take_IsClamped_ForGetTracksMissingStemsAsyncToo()
    {
        IReadOnlyList<Guid> clamped = await CreateQuery()
            .GetTracksMissingStemsAsync(
                _djLibraryId.ToString(),
                StemProducerVersion,
                PluginStemPolicy.Full,
                skip: 0,
                take: 0
            );

        clamped.Should().HaveCount(1);
    }
}
