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

using Microsoft.EntityFrameworkCore;
using NoMercyQueue.Core.Interfaces;

namespace NoMercyQueue.Sqlite;

public static class SqliteQueueContextFactory
{
    public static IQueueContext Create(string databasePath)
    {
        DbContextOptions<QueueDbContext> options = new DbContextOptionsBuilder<QueueDbContext>()
            .UseSqlite($"Data Source={databasePath}; Pooling=True; Foreign Keys=True;")
            .AddInterceptors(new SqliteQueueConnectionInterceptor())
            .Options;

        QueueDbContext dbContext = new(options);
        dbContext.Database.EnsureCreated();
        AddOriginalPayloadHashColumns(dbContext);

        return new SqliteQueueContext(dbContext);
    }

    // EnsureCreated never alters an existing file, so a database made before the
    // column existed gets it here, with current hashes as the backfill.
    private static void AddOriginalPayloadHashColumns(QueueDbContext dbContext)
    {
        bool jobsHave = dbContext
            .Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info('QueueJobs')")
            .ToList()
            .Contains("OriginalPayloadHash");
        if (!jobsHave)
        {
            dbContext.Database.ExecuteSql(
                $"ALTER TABLE QueueJobs ADD COLUMN OriginalPayloadHash TEXT NOT NULL DEFAULT ''"
            );
            dbContext.Database.ExecuteSql(
                $"UPDATE QueueJobs SET OriginalPayloadHash = PayloadHash"
            );
        }

        bool failedHave = dbContext
            .Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info('FailedJobs')")
            .ToList()
            .Contains("OriginalPayloadHash");
        if (!failedHave)
        {
            dbContext.Database.ExecuteSql(
                $"ALTER TABLE FailedJobs ADD COLUMN OriginalPayloadHash TEXT NOT NULL DEFAULT ''"
            );
        }
    }
}
