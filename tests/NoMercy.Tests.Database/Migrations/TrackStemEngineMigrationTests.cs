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
/// The stem register learns which engine made each stem (#637). Replayed the
/// way a self-hosted install sees it: stems stored before the column are all
/// kept, the ones the server split with Spleeter are marked as such, and a
/// plugin's own producer version stays unknown.
/// </summary>
public class TrackStemEngineMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261111235555_AddRevokedDevices";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"nm_stemengine_migration_{Guid.NewGuid():N}.db"
    );

    public void Dispose()
    {
        // Pooled handles keep the file locked on Windows past Dispose().
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
    public async Task StemsStoredBefore_AreKept_AndTheServersSpleeterStemsAreMarked()
    {
        Guid trackId = Guid.NewGuid();
        Ulid driverId = Ulid.NewUlid();
        Ulid folderId = Ulid.NewUlid();
        string spleeterKey = new('a', 64);
        string pluginKey = new('b', 64);
        DateTime now = DateTime.UtcNow;

        await using (MediaContext oldContext = OpenContext())
        {
            await oldContext.GetService<IMigrator>().MigrateAsync(PreviousMigration);

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
            await oldContext.SaveChangesAsync();

            // Raw SQL from here: the entities are the current model, and the
            // stem table here does not have the new column yet.
            await oldContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO Tracks (Id, Name, TrackNumber, DiscNumber, Duration, FolderId, CreatedAt, UpdatedAt)
                VALUES ({trackId}, {"A Track"}, {1}, {1}, {"03:45"}, {folderId.ToString()}, {now}, {now})
                """
            );

            foreach (string key in new[] { spleeterKey, pluginKey })
            {
                await oldContext.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO DerivedAudio (Key, ContentType, Bytes, CreatedAt, LastUsedAt)
                    VALUES ({key}, {"audio/ogg"}, {1}, {now}, {now})
                    """
                );
            }

            await InsertStemAsync(
                oldContext,
                trackId,
                spleeterKey,
                "spleeter-2stems-f16@9.0-NoMercy-MediaServer",
                now
            );
            await InsertStemAsync(oldContext, trackId, pluginKey, "my-own-splitter@3", now);
        }

        await using (MediaContext upgradeContext = OpenContext())
        {
            await upgradeContext.Database.MigrateAsync();
        }

        await using MediaContext readContext = OpenContext();
        List<TrackStem> stems = await readContext.TrackStems.AsNoTracking().ToListAsync();

        Assert.Equal(2, stems.Count);
        Assert.Equal("spleeter", stems.Single(stem => stem.StorageKey == spleeterKey).Engine);
        Assert.Null(stems.Single(stem => stem.StorageKey == pluginKey).Engine);
    }

    private static async Task InsertStemAsync(
        MediaContext context,
        Guid trackId,
        string key,
        string producerVersion,
        DateTime now
    )
    {
        string id = Ulid.NewUlid().ToString();

        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO TrackStems (Id, TrackId, Kind, Coverage, Format, SampleRate, StorageKey, ProducerVersion, CreatedAt)
            VALUES ({id}, {trackId}, {"vocals"}, {0}, {"opus"}, {48000}, {key}, {producerVersion}, {now})
            """
        );
    }
}
