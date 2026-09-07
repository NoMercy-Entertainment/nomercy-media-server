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
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

public class PluginMusicQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Ulid _libraryId = Ulid.NewUlid();
    private readonly Guid _analyzedTrackId = Guid.NewGuid();
    private readonly Guid _unanalyzedTrackId = Guid.NewGuid();
    private readonly Guid _failedTrackId = Guid.NewGuid();

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

        context.Libraries.Add(
            new Library
            {
                Id = _libraryId,
                Title = "Music",
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
            },
            new Track { Id = _unanalyzedTrackId, Name = "Unanalyzed" },
            new Track { Id = _failedTrackId, Name = "Failed" }
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

        context.SaveChanges();
    }

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

        return new PluginMusicQuery(factory.Object);
    }

    [Fact]
    public async Task GetTracksAsync_ReturnsTheLibrarysTracks()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(_libraryId.ToString());

        tracks.Should().HaveCount(3);
        tracks.Select(track => track.Id).Should().Contain(_analyzedTrackId);
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
}
