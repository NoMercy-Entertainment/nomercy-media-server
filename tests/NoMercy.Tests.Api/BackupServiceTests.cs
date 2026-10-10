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
using NoMercy.Api.Services;
using NoMercy.Database;
using NoMercy.Storage.Drivers.Local;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Unit")]
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"backup-set-{Guid.NewGuid():N}"
    );
    private readonly BackupService _service;

    public BackupServiceTests()
    {
        string data = Path.Combine(_root, "data");
        string config = Path.Combine(_root, "config");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        _service = new(new LocalStorageDriver())
        {
            BackupRoot = Path.Combine(data, "backups"),
            ConfigRoot = config,
            DatabasePaths = [Path.Combine(data, "app.db")],
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private SqliteConnection OpenDatabase()
    {
        SqliteConnection connection = new($"Data Source={_service.DatabasePaths[0]};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task CreateAsync_OpenWalWriter_ProducesConsistentDatabaseAndConfigSet()
    {
        using SqliteConnection writer = OpenDatabase();
        Execute(
            writer,
            "PRAGMA journal_mode=WAL; CREATE TABLE marker (value TEXT); INSERT INTO marker VALUES ('committed');"
        );
        using SqliteTransaction pending = writer.BeginTransaction();
        using (SqliteCommand insert = writer.CreateCommand())
        {
            insert.Transaction = pending;
            insert.CommandText = "INSERT INTO marker VALUES ('uncommitted');";
            insert.ExecuteNonQuery();
        }
        string nested = Path.Combine(_service.ConfigRoot, "seeds");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "settings.json"), "{\"value\":1}");

        string id = await _service.CreateAsync();

        using SqliteConnection backup = new(
            $"Data Source={Path.Combine(_service.BackupRoot, id, "app.db")};Pooling=False"
        );
        backup.Open();
        using SqliteCommand query = backup.CreateCommand();
        query.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", query.ExecuteScalar());
        query.CommandText = "SELECT value FROM marker";
        Assert.Equal("committed", query.ExecuteScalar());
        query.CommandText = "SELECT COUNT(*) FROM marker";
        Assert.Equal(1L, query.ExecuteScalar());
        Assert.Equal(
            "{\"value\":1}",
            File.ReadAllText(
                Path.Combine(_service.BackupRoot, id, "config", "seeds", "settings.json")
            )
        );
    }

    [Fact]
    public async Task RestoreAsync_NewerMigration_RefusesWithoutChangingLiveDatabase()
    {
        using SqliteConnection live = OpenDatabase();
        Execute(live, "CREATE TABLE marker (value TEXT); INSERT INTO marker VALUES ('live');");
        string id = await _service.CreateAsync();
        using SqliteConnection backup = new(
            $"Data Source={Path.Combine(_service.BackupRoot, id, "app.db")};Pooling=False"
        );
        backup.Open();
        Execute(
            backup,
            "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL); INSERT INTO __EFMigrationsHistory VALUES ('99999999999999_Future', '99.0.0');"
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RestoreAsync(id));
        using SqliteCommand query = live.CreateCommand();
        query.CommandText = "SELECT value FROM marker";
        Assert.Equal("live", query.ExecuteScalar());
    }

    [Fact]
    public async Task RestoreAsync_OlderAppSchema_AppliesEfMigration()
    {
        using SqliteConnection live = OpenDatabase();
        Execute(live, "CREATE TABLE old_marker (value TEXT);");
        string id = await _service.CreateAsync();

        await _service.RestoreAsync(id);

        using SqliteConnection restored = OpenDatabase();
        using SqliteCommand query = restored.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
        Assert.True(Convert.ToInt64(query.ExecuteScalar()) > 0);
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='Configuration'";
        Assert.Equal(1L, Convert.ToInt64(query.ExecuteScalar()));
    }

    [Fact]
    public async Task CreateAsync_RetentionKeepsLastNCompleteSets()
    {
        using SqliteConnection live = OpenDatabase();
        Execute(live, "CREATE TABLE marker (value TEXT);");
        _service.RetainCount = 2;
        string first = await _service.CreateAsync();
        await _service.CreateAsync();
        await _service.CreateAsync();

        IReadOnlyList<string> backups = await _service.ListAsync();
        Assert.Equal(2, backups.Count);
        Assert.DoesNotContain(first, backups);
    }

    [Fact]
    public async Task RestoreAsync_ReplacesConfigFolderContents()
    {
        using SqliteConnection live = OpenDatabase();
        Execute(live, "CREATE TABLE marker (value TEXT);");
        string settings = Path.Combine(_service.ConfigRoot, "settings.json");
        string stale = Path.Combine(_service.ConfigRoot, "stale.json");
        File.WriteAllText(settings, "before");
        string id = await _service.CreateAsync();
        File.WriteAllText(settings, "after");
        File.WriteAllText(stale, "stale");

        await _service.RestoreAsync(id);

        Assert.Equal("before", File.ReadAllText(settings));
        Assert.False(File.Exists(stale));
    }
}
