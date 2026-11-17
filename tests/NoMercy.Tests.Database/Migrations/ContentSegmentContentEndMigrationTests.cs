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
using NoMercy.Database.Models.Media;

namespace NoMercy.Tests.Database.Migrations;

/// <summary>
/// Content segments learn where the content really ends (#598). Replayed the
/// way a self-hosted install sees it: segments stored before the column are
/// kept and read null; the new column round-trips.
/// </summary>
public class ContentSegmentContentEndMigrationTests : IDisposable
{
    private const string PreviousMigration =
        "20261116192444_StripStaleStorageHostFromSpecialBackdrops";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"nm_contentend_migration_{Guid.NewGuid():N}.db"
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

    [Fact]
    public async Task SegmentsStoredBefore_AreKept_AndReadNoContentEnd()
    {
        string detectorId = Ulid.NewUlid().ToString();
        string manualId = Ulid.NewUlid().ToString();
        DateTime now = DateTime.UtcNow;

        await using (MediaContext oldContext = OpenContext())
        {
            await oldContext.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            // Raw SQL: the entity is the current model, the old table lacks the column.
            foreach (
                (string id, string source, int type, double start, double end) in new[]
                {
                    (detectorId, "detector", (int)ContentSegmentType.Outro, -240.0, -150.0),
                    (manualId, "manual", (int)ContentSegmentType.Intro, 5.0, 82.0),
                }
            )
            {
                await oldContext.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO ContentSegments (Id, SegmentType, StartSeconds, EndSeconds, Source, Confidence, CreatedAt, UpdatedAt)
                    VALUES ({id}, {type}, {start}, {end}, {source}, {1.0}, {now}, {now})
                    """
                );
            }
        }

        await using (MediaContext upgradeContext = OpenContext())
        {
            await upgradeContext.Database.MigrateAsync();
        }

        await using (MediaContext readContext = OpenContext())
        {
            List<ContentSegment> segments = await readContext
                .ContentSegments.AsNoTracking()
                .ToListAsync();

            Assert.Equal(2, segments.Count);
            Assert.All(segments, segment => Assert.Null(segment.ContentEndSeconds));
            Assert.Equal(-240.0, segments.Single(s => s.Source == "detector").StartSeconds);
        }

        await using (MediaContext writeContext = OpenContext())
        {
            writeContext.ContentSegments.Add(
                new ContentSegment
                {
                    SegmentType = ContentSegmentType.Credits,
                    StartSeconds = 1281.64,
                    EndSeconds = 1361.08,
                    ContentEndSeconds = 1377.27,
                    Source = "analysis",
                    Confidence = 0.9,
                }
            );
            await writeContext.SaveChangesAsync();
        }

        await using MediaContext finalContext = OpenContext();
        ContentSegment credits = await finalContext
            .ContentSegments.AsNoTracking()
            .SingleAsync(s => s.Source == "analysis");
        Assert.Equal(1377.27, credits.ContentEndSeconds);
    }
}
