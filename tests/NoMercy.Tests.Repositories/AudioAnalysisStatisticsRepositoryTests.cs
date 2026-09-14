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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Repositories")]
public class AudioAnalysisStatisticsRepositoryTests : IDisposable
{
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly AudioAnalysisStatisticsRepository _repository;

    public AudioAnalysisStatisticsRepositoryTests()
    {
        (IDbContextFactory<MediaContext> factory, SqliteConnection connection) =
            TestMediaContextFactory.CreateSeededFactory();
        _factory = factory;
        _connection = connection;
        _repository = new(_factory);
    }

    public void Dispose() => _connection.Dispose();

    private Track AddTrack()
    {
        using MediaContext context = _factory.CreateDbContext();
        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Track",
            FolderId = SeedConstants.MovieFolderId,
        };
        context.Tracks.Add(track);
        context.SaveChanges();
        return track;
    }

    [Fact]
    public async Task GetCountsAsync_CountsAnalysisRowsByState()
    {
        Track ok = AddTrack();
        Track failed = AddTrack();

        using (MediaContext context = _factory.CreateDbContext())
        {
            context.TrackAudioAnalysis.AddRange(
                new TrackAudioAnalysis
                {
                    TrackId = ok.Id,
                    State = AudioAnalysisState.Ok,
                    AnalyzedAt = DateTime.UtcNow,
                },
                new TrackAudioAnalysis
                {
                    TrackId = failed.Id,
                    State = AudioAnalysisState.Failed,
                    AnalyzedAt = DateTime.UtcNow,
                }
            );
            context.SaveChanges();
        }

        AudioAnalysisCounts counts = await _repository.GetCountsAsync();

        counts.Analyzed.Should().Be(1);
        counts.Failed.Should().Be(1);
    }

    [Fact]
    public async Task GetCountsAsync_CountsDjAnalysisRowsByState()
    {
        Track ok = AddTrack();
        Track failed = AddTrack();

        using (MediaContext context = _factory.CreateDbContext())
        {
            context.TrackDjAnalysis.AddRange(
                new TrackDjAnalysis
                {
                    TrackId = ok.Id,
                    ProducerPluginId = Ulid.NewUlid(),
                    State = AudioAnalysisState.Ok,
                    AnalyzedAt = DateTime.UtcNow,
                },
                new TrackDjAnalysis
                {
                    TrackId = failed.Id,
                    ProducerPluginId = Ulid.NewUlid(),
                    State = AudioAnalysisState.Failed,
                    AnalyzedAt = DateTime.UtcNow,
                }
            );
            context.SaveChanges();
        }

        AudioAnalysisCounts counts = await _repository.GetCountsAsync();

        counts.DjAnalyzed.Should().Be(1);
        counts.DjFailed.Should().Be(1);
    }

    [Fact]
    public async Task GetCountsAsync_SumsDerivedAudioBytes()
    {
        using (MediaContext context = _factory.CreateDbContext())
        {
            context.DerivedAudio.AddRange(
                new DerivedAudio
                {
                    Key = new('a', 64),
                    ContentType = "audio/flac",
                    Bytes = 1000,
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow,
                },
                new DerivedAudio
                {
                    Key = new('b', 64),
                    ContentType = "audio/flac",
                    Bytes = 500,
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow,
                }
            );
            context.SaveChanges();
        }

        AudioAnalysisCounts counts = await _repository.GetCountsAsync();

        counts.StemsBytes.Should().Be(1500);
    }

    [Fact]
    public async Task GetCountsAsync_ReturnsZeroes_WhenNothingIsAnalyzedYet()
    {
        AudioAnalysisCounts counts = await _repository.GetCountsAsync();

        counts.Analyzed.Should().Be(0);
        counts.Failed.Should().Be(0);
        counts.DjAnalyzed.Should().Be(0);
        counts.DjFailed.Should().Be(0);
        counts.StemsBytes.Should().Be(0);
    }
}
