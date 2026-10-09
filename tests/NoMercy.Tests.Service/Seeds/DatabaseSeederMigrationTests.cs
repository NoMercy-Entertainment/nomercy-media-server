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

using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NoMercy.Database;
using NoMercy.Service.Seeds;

namespace NoMercy.Tests.Service.Seeds;

public sealed class DatabaseSeederMigrationTests
{
    [Fact]
    public async Task ExistingTable_DoesNotStampFollowingColumnMigrationWithoutApplyingIt()
    {
        string databasePath = Path.Combine(
            AppContext.BaseDirectory,
            $"seeder-migration-{Guid.NewGuid():N}.db"
        );
        string backupRoot = Path.Combine(
            AppContext.BaseDirectory,
            $"seeder-backup-{Guid.NewGuid():N}"
        );
        string originalBackupRoot = DatabaseBackupService.BackupRoot;
        try
        {
            DatabaseBackupService.BackupRoot = backupRoot;
            DbContextOptionsBuilder<MediaContext> builder = new();
            builder.UseSqlite($"Data Source={databasePath};Pooling=False");
            await using MediaContext context = new(builder.Options);
            IMigrator migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260424203834_AddTrustedPublisherKeys");

            context.Database.ExecuteSqlRaw(
                "DELETE FROM __EFMigrationsHistory WHERE MigrationId = {0}",
                "20260424203834_AddTrustedPublisherKeys"
            );

            MethodInfo method = typeof(DatabaseSeeder).GetMethod(
                "Migrate",
                BindingFlags.NonPublic | BindingFlags.Static
            )!;
            Task migration = (Task)method.Invoke(null, [context])!;
            await migration;

            DbConnection connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync();

            using DbCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT name FROM pragma_table_info('Devices') WHERE name = 'Fingerprint'";
            Assert.Equal("Fingerprint", await command.ExecuteScalarAsync());
            Assert.Contains(
                "20260427025119_AddDevicePresence",
                context.Database.GetAppliedMigrations()
            );
        }
        finally
        {
            DatabaseBackupService.BackupRoot = originalBackupRoot;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
}
