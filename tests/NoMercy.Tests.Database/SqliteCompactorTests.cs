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
            DefaultTimeout = 1,
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

    private long DiskBytes(string fileName)
    {
        long bytes = new FileInfo(FilePath(fileName)).Length;
        FileInfo wal = new(FilePath(fileName) + "-wal");
        return wal.Exists ? bytes + wal.Length : bytes;
    }

    private SqliteConnection OpenReaderHoldingTheCurrentFile(string fileName)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = FilePath(fileName),
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly,
        };
        SqliteConnection reader = new(builder.ToString());
        reader.Open();
        Execute(reader, "BEGIN;");
        Scalar(reader, "SELECT COUNT(*) FROM Item;");
        return reader;
    }

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
    public async Task CompactIfWorthItAsync_ReaderHoldsTheOldFile_ReportsTheSizeOnDiskNotThePageCount()
    {
        await using SqliteConnection connection = OpenDatabase("reader.db");
        Fill(connection);
        Execute(connection, "DELETE FROM Item WHERE Id % 10 <> 0;");
        await using SqliteConnection reader = OpenReaderHoldingTheCurrentFile("reader.db");
        long diskBefore = DiskBytes("reader.db");

        SqliteCompactionResult result = await _compactor.CompactIfWorthItAsync(connection, 0.1, 0);

        result.BytesBefore.Should().Be(diskBefore);
        result.BytesAfter.Should().Be(DiskBytes("reader.db"));
    }

    [Fact]
    public async Task CompactIfWorthItAsync_ReaderOutlastsTheWait_ReportsBlockedAndNoSaving()
    {
        await using SqliteConnection connection = OpenDatabase("blocked.db");
        Fill(connection);
        Execute(connection, "DELETE FROM Item WHERE Id % 10 <> 0;");
        await using SqliteConnection reader = OpenReaderHoldingTheCurrentFile("blocked.db");
        long fileBefore = new FileInfo(FilePath("blocked.db")).Length;

        SqliteCompactionResult result = await _compactor.CompactIfWorthItAsync(
            connection,
            0.1,
            0,
            TimeSpan.FromMilliseconds(300)
        );

        result.CheckpointBlocked.Should().BeTrue();
        result.Compacted.Should().BeFalse();
        result.FreedBytes.Should().Be(0);
        new FileInfo(FilePath("blocked.db")).Length.Should().Be(fileBefore);
    }

    [Fact]
    public async Task CompactIfWorthItAsync_ReaderEndsDuringTheWait_ShrinksTheFileOnDisk()
    {
        await using SqliteConnection connection = OpenDatabase("released.db");
        Fill(connection);
        Execute(connection, "DELETE FROM Item WHERE Id % 10 <> 0;");
        long rowsBefore = Scalar(connection, "SELECT COUNT(*) FROM Item;");
        await using SqliteConnection reader = OpenReaderHoldingTheCurrentFile("released.db");
        long diskBefore = DiskBytes("released.db");
        Task release = Task.Run(async () =>
        {
            await Task.Delay(600);
            Execute(reader, "COMMIT;");
        });

        SqliteCompactionResult result = await _compactor.CompactIfWorthItAsync(
            connection,
            0.1,
            0,
            TimeSpan.FromSeconds(10)
        );
        await release;

        result.CheckpointBlocked.Should().BeFalse();
        result.Compacted.Should().BeTrue();
        DiskBytes("released.db").Should().BeLessThan(diskBefore);
        result.BytesAfter.Should().Be(DiskBytes("released.db"));
        result.FreedBytes.Should().Be(diskBefore - DiskBytes("released.db"));
        Scalar(connection, "SELECT COUNT(*) FROM Item;").Should().Be(rowsBefore);
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
