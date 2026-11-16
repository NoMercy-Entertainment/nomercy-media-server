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

namespace NoMercy.Data.ServerBackups;

public sealed class SqliteFileOperations : ISqliteFileOperations
{
    public void CopyOnline(string sourcePath, string destinationPath)
    {
        // Pooling off on both ends: a pooled connection keeps its file handle after
        // Dispose, which on Windows locks the copy against pruning and moving.
        using (SqliteConnection source = Open(sourcePath, SqliteOpenMode.ReadWrite))
        using (SqliteConnection destination = Open(destinationPath, SqliteOpenMode.ReadWriteCreate))
            source.BackupDatabase(destination);

        Seal(destinationPath);
    }

    public void Seal(string databasePath)
    {
        using SqliteConnection connection = Open(databasePath, SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<string> ReadAppliedMigrations(string databasePath)
    {
        using SqliteConnection connection = Open(databasePath, SqliteOpenMode.ReadOnly);

        using (SqliteCommand exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0)
                return [];
        }

        List<string> migrations = [];
        using SqliteCommand query = connection.CreateCommand();
        query.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
        using SqliteDataReader reader = query.ExecuteReader();
        while (reader.Read())
            migrations.Add(reader.GetString(0));

        return migrations;
    }

    public string? FindDamage(string databasePath)
    {
        try
        {
            using SqliteConnection connection = Open(databasePath, SqliteOpenMode.ReadOnly);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(5)";
            List<string> findings = [];
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                findings.Add(reader.GetString(0));

            return findings is ["ok"] ? null : string.Join("; ", findings);
        }
        catch (SqliteException exception)
        {
            // "file is not a database" and the like: damage too, with SQLite's word for it.
            return exception.Message;
        }
    }

    public void ReleaseAllConnections() => SqliteConnection.ClearAllPools();

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = mode,
                Pooling = false,
            }.ToString()
        );
        connection.Open();
        return connection;
    }
}
