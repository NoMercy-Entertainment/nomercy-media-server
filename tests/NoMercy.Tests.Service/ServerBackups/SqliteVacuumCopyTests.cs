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
using Microsoft.Extensions.Logging;
using NoMercy.Data.ServerBackups;

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class SqliteVacuumCopyTests : IDisposable
{
    private readonly BackupTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private sealed class LevelLogger : ILogger<SqliteFileOperations>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add((logLevel, formatter(state, exception)));
    }

    private static SqliteConnection OpenWal(string path)
    {
        SqliteConnection connection = new($"Data Source={path};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; "
            + "CREATE TABLE IF NOT EXISTS item (id INTEGER PRIMARY KEY, body BLOB);";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InsertRows(SqliteConnection connection, int count)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < $count) "
            + "INSERT INTO item (body) SELECT randomblob(2000) FROM n;";
        command.Parameters.AddWithValue("$count", count);
        command.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using SqliteConnection connection = new($"Data Source={path};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public void CopyOnline_OfADatabaseWithRowsInItsWal_HoldsEveryRowAndPassesTheIntegrityCheck()
    {
        string source = _folder.DatabasePath("media");
        string target = Path.Combine(_folder.Root, "copy.db");
        LevelLogger logger = new();
        using SqliteConnection writer = OpenWal(source);
        InsertRows(writer, 500);
        File.Exists(source + "-wal").Should().BeTrue();

        new SqliteFileOperations(logger).CopyOnline(source, target);

        Scalar(target, "SELECT COUNT(*) FROM item").Should().Be(500);
        using (SqliteConnection check = new($"Data Source={target};Pooling=False"))
        {
            check.Open();
            using SqliteCommand command = check.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            command.ExecuteScalar().Should().Be("ok");
        }
        logger.Lines.Should().Contain(line => line.Message.StartsWith("Vacuumed"));
        logger.Lines.Should().NotContain(line => line.Level >= LogLevel.Warning);
        File.Exists(target + "-wal").Should().BeFalse();
    }

    [Fact]
    public void WhenAnotherConnectionHoldsAWriteTransaction_TheCopyHoldsOnlyWhatWasCommitted()
    {
        string source = _folder.DatabasePath("media");
        string target = Path.Combine(_folder.Root, "copy.db");
        LevelLogger logger = new();
        using SqliteConnection writer = OpenWal(source);
        InsertRows(writer, 200);
        using SqliteCommand hold = writer.CreateCommand();
        hold.CommandText = "BEGIN IMMEDIATE";
        hold.ExecuteNonQuery();

        new SqliteFileOperations(logger).CopyOnline(source, target);

        Scalar(target, "SELECT COUNT(*) FROM item").Should().Be(200);
        logger.Lines.Should().NotContain(line => line.Level >= LogLevel.Warning);
    }
}
