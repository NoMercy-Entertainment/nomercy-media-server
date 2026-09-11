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
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Storage;

namespace NoMercy.Tests.Database.Models;

/// <summary>
/// The DJ record and the stem register hang off a track and off the derived
/// store: deleting either parent must take the rows with it, and a JSON column
/// must come back out of SQLite as the string that went in.
/// </summary>
public class AnalysisRecordModelTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<MediaContext> _options;

    public AnalysisRecordModelTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // Track.FolderId is a required FK to Folder, which is itself a required FK
    // to Driver, both enforced at the SQLite level (SqliteConnection enforces
    // foreign keys by default). A dangling FolderId — as a bare scalar would
    // leave it — fails the very first insert with "FOREIGN KEY constraint
    // failed" before any of this file's own tables are touched, so TrackRow
    // carries the whole chain through navigation properties for EF to insert
    // alongside the track.
    private static Track TrackRow(Guid id)
    {
        Ulid folderId = Ulid.NewUlid();
        return new Track
        {
            Id = id,
            Name = "A Track",
            Duration = "03:45",
            FolderId = folderId,
            LibraryFolder = new Folder
            {
                Id = folderId,
                Path = "/music",
                DriverId = Driver.SystemLocalDriverId,
                Driver = new Driver
                {
                    Id = Driver.SystemLocalDriverId,
                    Name = "local",
                    Type = "local",
                },
            },
        };
    }

    /// <summary>
    /// The five JSON columns are text to SQLite and text to EF: the writer
    /// serialises, the reader deserialises, and nothing in between is allowed
    /// to reformat, re-order or re-encode what was stored. A column that came
    /// back with its members in another order would still parse, and the
    /// reader would still answer - while no longer describing the same track.
    /// </summary>
    [Fact]
    public async Task AJsonColumn_ComesBackAsTheStringThatWentIn()
    {
        const string phraseStarts = "[0,15000,30000]";
        const string vocalRegions = "[[1000,5000],[9000,15000]]";
        const string barEnergy = "[-20.5,-18.2,-14]";
        const string cuePoints =
            "[{\"ms\":1000,\"type\":\"intro\",\"direction\":\"mixIn\",\"score\":0.9}]";
        const string chords = "[{\"ms\":0,\"chord\":\"Am\"}]";

        Guid trackId = Guid.NewGuid();
        await using (MediaContext context = new(_options))
        {
            context.Tracks.Add(TrackRow(trackId));
            context.TrackDjAnalysis.Add(
                new TrackDjAnalysis
                {
                    TrackId = trackId,
                    ProducerPluginId = Ulid.NewUlid(),
                    DjAnalyzerVersion = 1,
                    BaseAnalyzerVersion = 4,
                    State = AudioAnalysisState.Ok,
                    PhraseStartsMs = phraseStarts,
                    VocalRegionsMs = vocalRegions,
                    BarEnergy = barEnergy,
                    CuePoints = cuePoints,
                    Chords = chords,
                    AnalyzedAt = DateTime.UtcNow,
                }
            );
            await context.SaveChangesAsync();
        }

        await using MediaContext read = new(_options);
        TrackDjAnalysis row = await read.TrackDjAnalysis.AsNoTracking().SingleAsync();

        Assert.Equal(phraseStarts, row.PhraseStartsMs);
        Assert.Equal(vocalRegions, row.VocalRegionsMs);
        Assert.Equal(barEnergy, row.BarEnergy);
        Assert.Equal(cuePoints, row.CuePoints);
        Assert.Equal(chords, row.Chords);
    }

    [Fact]
    public async Task DeletingATrack_RemovesItsDjAnalysisAndStems()
    {
        Guid trackId = Guid.NewGuid();
        await using (MediaContext context = new(_options))
        {
            context.Tracks.Add(TrackRow(trackId));
            context.DerivedAudio.Add(
                new DerivedAudio
                {
                    Key = new string('a', 64),
                    ContentType = "audio/opus",
                    Bytes = 10,
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow,
                }
            );
            context.TrackDjAnalysis.Add(
                new TrackDjAnalysis
                {
                    TrackId = trackId,
                    ProducerPluginId = Ulid.NewUlid(),
                    DjAnalyzerVersion = 1,
                    BaseAnalyzerVersion = 4,
                    State = AudioAnalysisState.Ok,
                    PhraseStartsMs = "[0,15000]",
                    AnalyzedAt = DateTime.UtcNow,
                }
            );
            context.TrackStems.Add(
                new TrackStem
                {
                    Id = Ulid.NewUlid(),
                    TrackId = trackId,
                    Kind = "vocals",
                    Coverage = StemCoverage.Full,
                    Format = "opus",
                    SampleRate = 48000,
                    StorageKey = new string('a', 64),
                    ProducerVersion = "spleeter-2stems-f16@v1.0.41",
                    CreatedAt = DateTime.UtcNow,
                }
            );
            await context.SaveChangesAsync();
        }

        await using (MediaContext context = new(_options))
        {
            await context.Tracks.Where(track => track.Id == trackId).ExecuteDeleteAsync();
        }

        await using MediaContext read = new(_options);
        Assert.Empty(await read.TrackDjAnalysis.AsNoTracking().ToListAsync());
        Assert.Empty(await read.TrackStems.AsNoTracking().ToListAsync());
        Assert.Single(await read.DerivedAudio.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DeletingADerivedAudioRow_RemovesTheStemsThatPointAtIt()
    {
        Guid trackId = Guid.NewGuid();
        string key = new string('b', 64);
        await using (MediaContext context = new(_options))
        {
            context.Tracks.Add(TrackRow(trackId));
            context.DerivedAudio.Add(
                new DerivedAudio
                {
                    Key = key,
                    ContentType = "audio/opus",
                    Bytes = 10,
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow,
                }
            );
            context.TrackStems.Add(
                new TrackStem
                {
                    Id = Ulid.NewUlid(),
                    TrackId = trackId,
                    Kind = "vocals",
                    Coverage = StemCoverage.MixIn,
                    WindowStartMs = 0,
                    WindowEndMs = 45000,
                    Format = "opus",
                    SampleRate = 48000,
                    StorageKey = key,
                    ProducerVersion = "spleeter-2stems-f16@v1.0.41",
                    CreatedAt = DateTime.UtcNow,
                }
            );
            await context.SaveChangesAsync();
        }

        await using (MediaContext context = new(_options))
        {
            await context.DerivedAudio.Where(row => row.Key == key).ExecuteDeleteAsync();
        }

        await using MediaContext read = new(_options);
        Assert.Empty(await read.TrackStems.AsNoTracking().ToListAsync());
        Assert.Single(await read.Tracks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TheSameStemTwice_IsRejectedByTheUniqueIndex()
    {
        Guid trackId = Guid.NewGuid();
        string key = new string('c', 64);
        await using MediaContext context = new(_options);
        context.Tracks.Add(TrackRow(trackId));
        context.DerivedAudio.Add(
            new DerivedAudio
            {
                Key = key,
                ContentType = "audio/opus",
                Bytes = 10,
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow,
            }
        );
        TrackStem Stem() =>
            new()
            {
                Id = Ulid.NewUlid(),
                TrackId = trackId,
                Kind = "vocals",
                Coverage = StemCoverage.Full,
                Format = "opus",
                SampleRate = 48000,
                StorageKey = key,
                ProducerVersion = "p@1",
                CreatedAt = DateTime.UtcNow,
            };
        context.TrackStems.Add(Stem());
        context.TrackStems.Add(Stem());

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
