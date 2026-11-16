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

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// During a long import a signed-in read must not wait for the writer, and the WAL
/// must stay bounded even though some client is always reading (issue #672).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReadsDuringLongWriteTests : IDisposable
{
    private static readonly TimeSpan WriterHold = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(2);
    private const int BurstRows = 3000;
    private const long WalLimitBytes = 8L * 1024 * 1024;
    private const long WalLimitFrames = WalLimitBytes / 4096;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly TvShowRepository _repository;

    public ReadsDuringLongWriteTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"nomercy-reads-during-write-{Guid.NewGuid():N}.db"
        );
        _connectionString = $"Data Source={_databasePath}; Pooling=True; Foreign Keys=True;";

        // Mirrors the server's MediaContext: a pooled file database with the same
        // split-query setting, execution strategy and connection interceptors.
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(
                _connectionString,
                o =>
                {
                    o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                    o.ExecutionStrategy(deps => new SqliteRetryingExecutionStrategy(deps));
                }
            )
            .AddInterceptors([
                new EntityBaseUpdatedAtInterceptor(),
                new SqliteNormalizeSearchInterceptor(),
                new SqliteConnectionInterceptor(),
            ])
            .Options;

        using (TestMediaContext context = new(options))
        {
            context.Database.EnsureCreated();
            TestMediaContextFactory.SeedData(context);
        }

        _factory = new TestDbContextFactory(options);
        _repository = new(_factory, new StubMediaTypeClassifier());
    }

    [Fact]
    public async Task GetTvAsync_ReturnsWithinBudget_WhileAWriterHoldsTheWriteLock()
    {
        // Compile the query first: the budget measures waiting on the lock, not a cold EF model.
        await _repository.GetTvAsync(SeedConstants.UserId, 1399, "en", "US");

        TimeSpan elapsed = await TimeReadDuringWriteAsync(async () =>
        {
            TvDetail? detail = await _repository.GetTvAsync(SeedConstants.UserId, 1399, "en", "US");
            Assert.NotNull(detail);
        });

        Assert.True(
            elapsed < ReadBudget,
            $"GetTvAsync took {elapsed.TotalMilliseconds:F0} ms while a writer held the lock; budget {ReadBudget.TotalMilliseconds:F0} ms"
        );
    }

    [Fact]
    public async Task Wal_StaysUnderTheLimit_AfterAWriteBurstWithOverlappingReaders()
    {
        await WriteBurstWithOverlappingReadersAsync();

        WalCheckpointResult result = await new SqliteWalCheckpointer().TruncateIfOverLimitAsync(
            _databasePath,
            WalLimitBytes
        );

        long walFrames = await ReadWalFramesAsync();

        Assert.True(
            walFrames <= WalLimitFrames,
            $"WAL holds {walFrames} frames after the write burst; limit {WalLimitFrames} frames"
        );
        Assert.True(result.OverLimit);
        Assert.True(result.Truncated);
        Assert.Equal(0, new FileInfo(_databasePath + "-wal").Length);
    }

    [Fact]
    public async Task Wal_IsLeftAlone_WhileItIsUnderTheLimit()
    {
        WalCheckpointResult result = await new SqliteWalCheckpointer().TruncateIfOverLimitAsync(
            _databasePath,
            WalLimitBytes
        );

        Assert.False(result.OverLimit);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Wal_RestartIsReportedBlocked_WhileAReaderStaysOpen()
    {
        await WriteBurstWithOverlappingReadersAsync();

        await using SqliteConnection pinnedReader = new(_connectionString);
        await pinnedReader.OpenAsync();
        await ExecuteAsync(pinnedReader, "BEGIN; SELECT COUNT(*) FROM Tvs;");
        await using SqliteConnection writer = new(_connectionString);
        await writer.OpenAsync();
        await ExecuteAsync(writer, "INSERT INTO WalFiller (Payload) VALUES (randomblob(4096));");

        WalCheckpointResult result = await new SqliteWalCheckpointer().TruncateIfOverLimitAsync(
            _databasePath,
            WalLimitBytes
        );
        await ExecuteAsync(pinnedReader, "COMMIT;");

        Assert.True(result.OverLimit);
        Assert.False(result.Truncated);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in (string[])["", "-wal", "-shm"])
        {
            string path = _databasePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private async Task<TimeSpan> TimeReadDuringWriteAsync(Func<Task> read)
    {
        await using SqliteConnection writer = new(_connectionString);
        await writer.OpenAsync();
        await ExecuteAsync(writer, "BEGIN IMMEDIATE;");
        await ExecuteAsync(writer, "UPDATE Tvs SET Title = Title;");

        Task release = Task.Run(async () =>
        {
            await Task.Delay(WriterHold);
            await ExecuteAsync(writer, "COMMIT;");
        });

        Stopwatch stopwatch = Stopwatch.StartNew();
        await read();
        stopwatch.Stop();

        await release;
        return stopwatch.Elapsed;
    }

    // Some reader is always inside a read transaction, as clients are during an import,
    // so SQLite's own automatic checkpoint never gets to restart the WAL.
    private async Task WriteBurstWithOverlappingReadersAsync()
    {
        await using SqliteConnection writer = new(_connectionString);
        await writer.OpenAsync();
        await ExecuteAsync(
            writer,
            "CREATE TABLE WalFiller (Id INTEGER PRIMARY KEY, Payload BLOB NOT NULL);"
        );

        await using SqliteConnection firstReader = new(_connectionString);
        await using SqliteConnection secondReader = new(_connectionString);
        await firstReader.OpenAsync();
        await secondReader.OpenAsync();

        SqliteConnection activeReader = firstReader;
        SqliteConnection idleReader = secondReader;
        await ExecuteAsync(activeReader, "BEGIN; SELECT COUNT(*) FROM Tvs;");

        for (int row = 0; row < BurstRows; row++)
        {
            await ExecuteAsync(
                writer,
                "INSERT INTO WalFiller (Payload) VALUES (randomblob(4096));"
            );

            if (row % 200 != 199)
                continue;

            await ExecuteAsync(idleReader, "BEGIN; SELECT COUNT(*) FROM Tvs;");
            await ExecuteAsync(activeReader, "COMMIT;");
            (activeReader, idleReader) = (idleReader, activeReader);
        }

        await ExecuteAsync(activeReader, "COMMIT;");
    }

    private async Task<long> ReadWalFramesAsync()
    {
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetInt64(1);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
