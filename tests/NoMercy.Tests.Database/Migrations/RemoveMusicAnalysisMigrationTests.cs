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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;

namespace NoMercy.Tests.Database.Migrations;

/// <summary>
/// Music analysis leaves the server (#689): four tables and the per-library
/// switch go. Replayed the way a self-hosted install sees it: a server that
/// has analysis rows loses them and nothing else, a fresh install never gets
/// the tables, and a server rolled back to the older version gets them back
/// empty.
/// </summary>
[Trait("Category", "Integration")]
public class RemoveMusicAnalysisMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261117043422_AddContentSegmentContentEnd";

    private static readonly string[] RemovedTables =
    [
        "TrackAudioAnalysis",
        "TrackDjAnalysis",
        "TrackStems",
        "DerivedAudio",
    ];

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"nm_remove_music_analysis_migration_{Guid.NewGuid():N}.db"
    );

    public void Dispose()
    {
        // Pooled handles keep the file locked on Windows past Dispose().
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
            File.Delete(_databasePath);

        GC.SuppressFinalize(this);
    }

    private MediaContext OpenContext()
    {
        DbContextOptionsBuilder<MediaContext> builder = new();
        builder.UseSqlite($"Data Source={_databasePath}");

        return new(builder.Options);
    }

    private static Task<List<string>> TableNamesAsync(MediaContext context) =>
        context
            .Database.SqlQuery<string>(
                $"SELECT name AS Value FROM sqlite_master WHERE type = 'table'"
            )
            .ToListAsync();

    private static Task<List<string>> ColumnNamesAsync(MediaContext context, string table) =>
        context
            .Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({table})")
            .ToListAsync();

    private static Task<List<string>> IndexNamesAsync(MediaContext context, string table) =>
        context
            .Database.SqlQuery<string>(
                $"SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = {table}"
            )
            .ToListAsync();

    [Fact]
    public async Task AFreshInstall_HasNoneOfTheFourTables()
    {
        await using MediaContext context = OpenContext();
        await context.Database.MigrateAsync();

        (await TableNamesAsync(context)).Should().NotIntersectWith(RemovedTables);
        (await ColumnNamesAsync(context, "Libraries")).Should().NotContain("AnalyzeAudio");
    }

    [Fact]
    public async Task AnUpdatedServer_LosesItsAnalysis_AndKeepsItsLibrariesAndTracks()
    {
        Ulid libraryId = Ulid.NewUlid();
        Ulid driverId = Ulid.NewUlid();
        Ulid folderId = Ulid.NewUlid();
        Guid trackId = Guid.NewGuid();
        string storageKey = new('a', 64);
        DateTime now = DateTime.UtcNow;
        List<string> libraryIndexesBefore;

        await using (MediaContext oldContext = OpenContext())
        {
            await oldContext.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            oldContext.Libraries.Add(
                new()
                {
                    Id = libraryId,
                    Title = "Music",
                    Type = "music",
                }
            );
            oldContext.Drivers.Add(
                new()
                {
                    Id = driverId,
                    Name = "Media",
                    Type = "local",
                }
            );
            oldContext.Folders.Add(
                new()
                {
                    Id = folderId,
                    DriverId = driverId,
                    Path = "Libraries/Music",
                }
            );
            oldContext.Tracks.Add(
                new()
                {
                    Id = trackId,
                    Name = "A track",
                    FolderId = folderId,
                }
            );
            oldContext.LibraryTrack.Add(new() { LibraryId = libraryId, TrackId = trackId });
            await oldContext.SaveChangesAsync();

            // Raw SQL: the entities for these rows no longer exist.
            await oldContext.Database.ExecuteSqlAsync($"UPDATE Libraries SET AnalyzeAudio = 0");
            await oldContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO DerivedAudio (Key, Bytes, ContentType, LastUsedAt)
                VALUES ({storageKey}, {10}, {"audio/opus"}, {now})
                """
            );
            await oldContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO TrackAudioAnalysis (TrackId, AnalyzedAt, AnalyzerVersion, State)
                VALUES ({trackId}, {now}, {1}, {1})
                """
            );
            await oldContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO TrackDjAnalysis (TrackId, AnalyzedAt, BaseAnalyzerVersion, BeatsPerBar, DjAnalyzerVersion, PhraseLengthBars, ProducerPluginId, State)
                VALUES ({trackId}, {now}, {1}, {4}, {1}, {8}, {"a.plugin"}, {1})
                """
            );
            await oldContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO TrackStems (Id, StorageKey, TrackId, Coverage, Format, Kind, ProducerVersion, SampleRate)
                VALUES ({Ulid.NewUlid().ToString()}, {storageKey}, {trackId}, {0}, {"opus"}, {"vocals"}, {"1"}, {48000})
                """
            );

            libraryIndexesBefore = await IndexNamesAsync(oldContext, "Libraries");
        }

        await using (MediaContext upgradeContext = OpenContext())
        {
            await upgradeContext.Database.MigrateAsync();
        }

        await using MediaContext readContext = OpenContext();

        (await TableNamesAsync(readContext)).Should().NotIntersectWith(RemovedTables);
        (await ColumnNamesAsync(readContext, "Libraries")).Should().NotContain("AnalyzeAudio");

        // Taking a column out rebuilds the table. Nothing else of it may go.
        (await IndexNamesAsync(readContext, "Libraries"))
            .Should()
            .BeEquivalentTo(libraryIndexesBefore);

        Library library = await readContext.Libraries.AsNoTracking().SingleAsync();
        library.Id.Should().Be(libraryId);
        library.Title.Should().Be("Music");
        library.Type.Should().Be("music");

        Track track = await readContext.Tracks.AsNoTracking().SingleAsync();
        track.Id.Should().Be(trackId);
        track.Name.Should().Be("A track");

        LibraryTrack link = await readContext.LibraryTrack.AsNoTracking().SingleAsync();
        link.LibraryId.Should().Be(libraryId);
        link.TrackId.Should().Be(trackId);

        List<string> brokenLinks = await readContext
            .Database.SqlQuery<string>($"SELECT \"table\" AS Value FROM pragma_foreign_key_check")
            .ToListAsync();
        brokenLinks.Should().BeEmpty();
    }

    [Fact]
    public async Task AServerRolledBack_GetsTheTablesBackEmpty_AndAnalysisOn()
    {
        await using (MediaContext context = OpenContext())
        {
            await context.Database.MigrateAsync();

            context.Libraries.Add(
                new()
                {
                    Id = Ulid.NewUlid(),
                    Title = "Music",
                    Type = "music",
                }
            );
            await context.SaveChangesAsync();
        }

        await using MediaContext oldContext = OpenContext();
        await oldContext.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        (await TableNamesAsync(oldContext)).Should().Contain(RemovedTables);

        int analysisRows = await oldContext
            .Database.SqlQuery<int>(
                $"""
                SELECT (SELECT COUNT(*) FROM TrackAudioAnalysis)
                     + (SELECT COUNT(*) FROM TrackDjAnalysis)
                     + (SELECT COUNT(*) FROM TrackStems)
                     + (SELECT COUNT(*) FROM DerivedAudio) AS Value
                """
            )
            .SingleAsync();
        analysisRows.Should().Be(0);

        // The older server measures again, which is slow and not broken.
        List<int> switches = await oldContext
            .Database.SqlQuery<int>($"SELECT AnalyzeAudio AS Value FROM Libraries")
            .ToListAsync();
        switches.Should().Equal(1);
    }
}
