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
using NoMercy.Data.ServerBackups;
using NoMercy.Database;

namespace NoMercy.Service.Seeds;

/// <summary>
/// Maps the database names of a backup set to this server's EF Core contexts.
/// Migrating a staged copy runs the same code the boot migration runs, repairs
/// included, so a restored database gets no other treatment than an upgrade.
/// </summary>
public sealed class BackupDatabaseSchema : IBackupDatabaseSchema
{
    public IReadOnlyList<string> KnownMigrations(string databaseName)
    {
        // An in-memory connection that is never opened: listing migrations only
        // reads this assembly, and no file may be created or touched for it.
        using DbContext context = CreateContext(databaseName, "Data Source=:memory:");
        return context.Database.GetMigrations().ToList();
    }

    public async Task MigrateAsync(
        string databaseName,
        string databasePath,
        CancellationToken cancellationToken
    )
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

        await using DbContext context = CreateContext(databaseName, connectionString);
        await DatabaseSeeder.MigrateCopy(context);

        List<string> pending = (
            await context.Database.GetPendingMigrationsAsync(cancellationToken)
        ).ToList();
        if (pending.Count > 0)
            throw new InvalidOperationException(
                $"The {databaseName} database could not be brought up to date; "
                    + $"still pending: {string.Join(", ", pending)}."
            );
    }

    private static DbContext CreateContext(string databaseName, string connectionString) =>
        databaseName switch
        {
            "app" => new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options
            ),
            "media" => new MediaContext(
                new DbContextOptionsBuilder<MediaContext>().UseSqlite(connectionString).Options
            ),
            "queue" => new QueueContext(
                new DbContextOptionsBuilder<QueueContext>().UseSqlite(connectionString).Options
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(databaseName),
                databaseName,
                "No database by that name."
            ),
        };
}
