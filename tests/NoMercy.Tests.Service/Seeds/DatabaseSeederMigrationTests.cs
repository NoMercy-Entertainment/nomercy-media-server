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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Data.ServerBackups;
using NoMercy.Database;
using NoMercy.Service.Seeds;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Service.Seeds;

public sealed class DatabaseSeederMigrationTests
{
    private const string PlaylistItemMigration = "20260709132315_AddPlaylistItem";

    [Fact]
    public async Task ExistingTable_DoesNotStampFollowingColumnMigrationWithoutApplyingIt()
    {
        await RunMigrateAfterUnstampingAsync(
            "20260424203834_AddTrustedPublisherKeys",
            beforeMigrate: _ => Task.CompletedTask,
            afterMigrate: async context =>
            {
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
        );
    }

    [Fact]
    public async Task UnsupportedOperations_StampWhenObjectsExistByName()
    {
        await RunMigrateAfterUnstampingAsync(
            PlaylistItemMigration,
            beforeMigrate: _ => Task.CompletedTask,
            afterMigrate: context =>
            {
                Assert.Contains(PlaylistItemMigration, context.Database.GetAppliedMigrations());
                Assert.Empty(context.Database.GetPendingMigrations());
                return Task.CompletedTask;
            }
        );
    }

    [Fact]
    public async Task UnsupportedOperations_RethrowWhenAnObjectIsMissing()
    {
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            RunMigrateAfterUnstampingAsync(
                PlaylistItemMigration,
                beforeMigrate: context =>
                {
                    context.Database.ExecuteSqlRaw("DROP INDEX IX_PlaylistItems_MovieId");
                    return Task.CompletedTask;
                },
                afterMigrate: _ => Task.CompletedTask
            )
        );

        Assert.Contains("already exists", exception.Message);
    }

    private static async Task RunMigrateAfterUnstampingAsync(
        string migrationId,
        Func<MediaContext, Task> beforeMigrate,
        Func<MediaContext, Task> afterMigrate
    )
    {
        string databasePath = Path.Combine(
            AppContext.BaseDirectory,
            $"seeder-migration-{Guid.NewGuid():N}.db"
        );
        string backupRoot = Path.Combine(
            AppContext.BaseDirectory,
            $"seeder-backup-{Guid.NewGuid():N}"
        );
        PreMigrationBackup preMigrationBackup = new(
            new LocalStorageDriver(),
            new SqliteFileOperations(),
            new BackupOptions
            {
                BackupRoot = backupRoot,
                ConfigRoot = backupRoot,
                Databases = [],
            },
            TimeProvider.System,
            NullLogger<PreMigrationBackup>.Instance
        );
        try
        {
            DbContextOptionsBuilder<MediaContext> builder = new();
            builder.UseSqlite($"Data Source={databasePath};Pooling=False");
            await using MediaContext context = new(builder.Options);
            IMigrator migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrationId);

            context.Database.ExecuteSqlRaw(
                "DELETE FROM __EFMigrationsHistory WHERE MigrationId = {0}",
                migrationId
            );
            await beforeMigrate(context);

            MethodInfo method = typeof(DatabaseSeeder).GetMethod(
                "Migrate",
                BindingFlags.NonPublic | BindingFlags.Static
            )!;
            try
            {
                await (Task)method.Invoke(null, [context, preMigrationBackup])!;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }

            await afterMigrate(context);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
}
