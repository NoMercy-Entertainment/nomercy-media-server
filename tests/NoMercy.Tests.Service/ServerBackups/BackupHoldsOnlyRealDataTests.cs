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
using NoMercy.Data.ServerBackups;

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class BackupHoldsOnlyRealDataTests : IDisposable
{
    private readonly BackupTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void CopyOnline_OfADatabaseWithFreePages_IsSmallerThanTheSourceAndIntact()
    {
        string source = _folder.DatabasePath("media");
        string copy = Path.Combine(_folder.DataRoot, "media.copy.db");
        CreateDatabaseWithFreePages(source);
        long sourceBytes = new FileInfo(source).Length;

        new SqliteFileOperations().CopyOnline(source, copy);

        new FileInfo(copy).Length.Should().BeLessThan(sourceBytes / 2);
        new SqliteFileOperations().FindDamage(copy).Should().BeNull();
        ReadRowCount(copy).Should().Be(ReadRowCount(source));
    }

    [Fact]
    public void CopyOnline_OfAWalDatabase_KeepsRowsStillInTheWal()
    {
        string source = _folder.DatabasePath("media");
        string copy = Path.Combine(_folder.DataRoot, "media.copy.db");
        CreateDatabaseWithFreePages(source);
        using SqliteConnection live = new($"Data Source={source};Pooling=False");
        live.Open();
        Execute(live, "PRAGMA journal_mode=WAL");
        Execute(live, "INSERT INTO blobs (data) VALUES (randomblob(10))");
        long rows = ReadRowCount(source);

        new SqliteFileOperations().CopyOnline(source, copy);

        ReadRowCount(copy).Should().Be(rows);
        File.Exists(copy + "-wal").Should().BeFalse();
    }

    [Fact]
    public async Task ASetOfADatabaseWithFreePages_HoldsOnlyTheRowsInUse()
    {
        _folder.CreateServer();
        CreateDatabaseWithFreePages(_folder.DatabasePath("media"), replace: true);
        long liveBytes = new FileInfo(_folder.DatabasePath("media")).Length;

        BackupSetSummary set = await BackupTestFolder
            .CreateBackupService(_folder.Options())
            .CreateAsync("test", CancellationToken.None);

        string saved = Path.Combine(_folder.BackupRoot, "sets", set.Id, "databases", "media.db");
        new FileInfo(saved).Length.Should().BeLessThan(liveBytes / 2);
    }

    private static void CreateDatabaseWithFreePages(string path, bool replace = false)
    {
        if (replace)
            File.Delete(path);

        using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        connection.Open();
        Execute(
            connection,
            "CREATE TABLE blobs (id INTEGER PRIMARY KEY, data BLOB NOT NULL);"
                + "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);"
        );
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            for (int i = 0; i < 2000; i++)
                Execute(connection, "INSERT INTO blobs (data) VALUES (randomblob(2048))");
            transaction.Commit();
        }

        Execute(connection, "DELETE FROM blobs WHERE id > 20");
    }

    private static long ReadRowCount(string path)
    {
        using SqliteConnection connection = new($"Data Source={path};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM blobs";
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
