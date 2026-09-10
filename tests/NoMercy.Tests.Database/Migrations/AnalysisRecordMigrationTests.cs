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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Storage;

namespace NoMercy.Tests.Database.Migrations;

/// <summary>
/// The DJ record, stem register and derived-audio register are new tables on
/// an existing database, not a rewrite of one. This replays the upgrade the
/// way a self-hosted install sees it: a database that predates the change,
/// carrying a track and its base analysis row, then the new migration on top
/// of it — the track and its row must survive, and the three new tables must
/// exist and be empty.
/// </summary>
public class AnalysisRecordMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260909230447_AnalyzeAudioDefaultsOn";

    private readonly string _databasePath;

    public AnalysisRecordMigrationTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"nm_analysisrecord_migration_{Guid.NewGuid():N}.db"
        );
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools physical file handles across connections
        // even after Dispose(); on Windows the OS-level lock outlives the
        // `using` above, so the delete fails unless the pool is cleared first.
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    private MediaContext OpenContext()
    {
        DbContextOptionsBuilder<MediaContext> builder = new();
        builder.UseSqlite($"Data Source={_databasePath}");

        return new(builder.Options);
    }

    [Fact]
    public async Task AnUpgradedDatabase_KeepsItsRowsAndGainsTheEmptyTables()
    {
        Guid trackId = Guid.NewGuid();
        Ulid driverId = Ulid.NewUlid();
        Ulid folderId = Ulid.NewUlid();

        await using (MediaContext oldContext = OpenContext())
        {
            IMigrator migrator = oldContext.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            oldContext.Add(
                new Driver
                {
                    Id = driverId,
                    Name = "local",
                    Type = "local",
                }
            );
            oldContext.Add(
                new Folder
                {
                    Id = folderId,
                    Path = "/music",
                    DriverId = driverId,
                }
            );
            oldContext.Add(
                new Track
                {
                    Id = trackId,
                    Name = "A Track",
                    Duration = "03:45",
                    FolderId = folderId,
                }
            );
            oldContext.Add(
                new TrackAudioAnalysis
                {
                    TrackId = trackId,
                    AnalyzerVersion = 1,
                    State = AudioAnalysisState.Ok,
                    AnalyzedAt = DateTime.UtcNow,
                }
            );

            await oldContext.SaveChangesAsync();
        }

        await using (MediaContext upgradeContext = OpenContext())
        {
            await upgradeContext.Database.MigrateAsync();
        }

        await using MediaContext readContext = OpenContext();

        Track track = await readContext.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.Equal("A Track", track.Name);

        TrackAudioAnalysis baseAnalysis = await readContext
            .TrackAudioAnalysis.AsNoTracking()
            .SingleAsync(a => a.TrackId == trackId);
        Assert.Equal(AudioAnalysisState.Ok, baseAnalysis.State);

        Assert.Empty(await readContext.TrackDjAnalysis.AsNoTracking().ToListAsync());
        Assert.Empty(await readContext.TrackStems.AsNoTracking().ToListAsync());
        Assert.Empty(await readContext.DerivedAudio.AsNoTracking().ToListAsync());
    }
}
