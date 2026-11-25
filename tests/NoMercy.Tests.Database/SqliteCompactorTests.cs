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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using NoMercy.Database;
using Xunit;

namespace NoMercy.Tests.Database;

public class SqliteCompactorTests : IDisposable
{
    private const int RowCount = 400;
    private const int PayloadBytes = 8000;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        Guid.NewGuid().ToString()
    );
    private readonly SqliteCompactor _compactor = new();

    public SqliteCompactorTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private SqliteConnection OpenDatabase(string fileName)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = FilePath(fileName),
            Pooling = false,
        };
        SqliteConnection connection = new(builder.ToString());
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "CREATE TABLE Item (Id INTEGER PRIMARY KEY, Payload BLOB NOT NULL);");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Fill(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Item (Payload) VALUES (zeroblob($size));";
        command.Parameters.AddWithValue("$size", PayloadBytes);
        for (int i = 0; i < RowCount; i++)
            command.ExecuteNonQuery();
        transaction.Commit();
    }

    private string FilePath(string fileName) => Path.Combine(_directory, fileName);

    [Fact]
    public async Task CompactIfWorthItAsync_FreePagesOverThreshold_ShrinksFileAndKeepsRows()
    {
        await using SqliteConnection connection = OpenDatabase("over.db");
        Fill(connection);
        Execute(connection, "DELETE FROM Item WHERE Id % 10 <> 0;");
        long freeBefore = Scalar(connection, "PRAGMA freelist_count;");
        long rowsBefore = Scalar(connection, "SELECT COUNT(*) FROM Item;");

        SqliteCompactionResult result = await _compactor.CompactIfWorthItAsync(connection, 0.1, 0);

        freeBefore.Should().BeGreaterThan(0);
        result.Compacted.Should().BeTrue();
        result.BytesAfter.Should().BeLessThan(result.BytesBefore);
        Scalar(connection, "PRAGMA freelist_count;").Should().Be(0);
        Scalar(connection, "SELECT COUNT(*) FROM Item;").Should().Be(rowsBefore);
        new FileInfo(FilePath("over.db")).Length.Should().Be(result.BytesAfter);
    }

    [Fact]
    public async Task CompactIfWorthItAsync_FreePagesBelowThreshold_LeavesDatabaseUntouched()
    {
        await using SqliteConnection connection = OpenDatabase("below.db");
        Fill(connection);
        Execute(connection, "DELETE FROM Item WHERE Id % 10 = 0;");
        long freeBefore = Scalar(connection, "PRAGMA freelist_count;");

        SqliteCompactionResult result = await _compactor.CompactIfWorthItAsync(connection, 0.5, 0);

        freeBefore.Should().BeGreaterThan(0);
        result.Compacted.Should().BeFalse();
        Scalar(connection, "PRAGMA freelist_count;").Should().Be(freeBefore);
    }

    [Fact]
    public async Task CompactIfWorthItAsync_FreeBytesBelowMinimum_LeavesDatabaseUntouched()
    {
        await using SqliteConnection connection = OpenDatabase("minimum.db");
        Fill(connection);
        Execute(connection, "DELETE FROM Item;");
        long freeBefore = Scalar(connection, "PRAGMA freelist_count;");

        SqliteCompactionResult result = await _compactor.CompactIfWorthItAsync(
            connection,
            0.1,
            long.MaxValue
        );

        result.Compacted.Should().BeFalse();
        Scalar(connection, "PRAGMA freelist_count;").Should().Be(freeBefore);
    }

    [Fact]
    public async Task CompactIfWorthItAsync_SecondRun_ReclaimsAfterTheConversionToIncremental()
    {
        await using SqliteConnection connection = OpenDatabase("again.db");
        Fill(connection);
        Execute(connection, "DELETE FROM Item WHERE Id % 2 = 0;");
        await _compactor.CompactIfWorthItAsync(connection, 0.1, 0);
        Scalar(connection, "PRAGMA auto_vacuum;").Should().Be(2);
        Execute(connection, "DELETE FROM Item WHERE Id % 4 = 1;");
        long rowsBefore = Scalar(connection, "SELECT COUNT(*) FROM Item;");

        SqliteCompactionResult result = await _compactor.CompactIfWorthItAsync(connection, 0.1, 0);

        result.Compacted.Should().BeTrue();
        result.BytesAfter.Should().BeLessThan(result.BytesBefore);
        Scalar(connection, "PRAGMA freelist_count;").Should().Be(0);
        Scalar(connection, "SELECT COUNT(*) FROM Item;").Should().Be(rowsBefore);
    }
}
