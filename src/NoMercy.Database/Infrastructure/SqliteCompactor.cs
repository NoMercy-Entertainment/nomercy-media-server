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

using System.Data;
using System.Data.Common;

namespace NoMercy.Database;

/// <summary>
/// Returns free pages of a SQLite file to the file system. SQLite never shrinks a file
/// on its own: deleted rows leave free pages behind that only a VACUUM gives back.
/// </summary>
public sealed class SqliteCompactor
{
    private const long IncrementalAutoVacuum = 2;

    // Pacing of the retry only; how long to keep retrying is the caller's checkpointWait setting.
    private static readonly TimeSpan CheckpointPollInterval = TimeSpan.FromMilliseconds(250);
    private const int WalRestartBusyTimeoutMilliseconds = 200;

    // Sizes are on disk (file plus WAL); a reader older than the rebuild blocks the shrink.
    public async Task<SqliteCompactionResult> CompactIfWorthItAsync(
        DbConnection connection,
        double minFreeRatio,
        long minFreeBytes,
        TimeSpan checkpointWait = default,
        CancellationToken cancellationToken = default
    )
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        long pageSize = await ScalarAsync(connection, "PRAGMA page_size;", cancellationToken);
        long pageCount = await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken);
        long freePages = await ScalarAsync(connection, "PRAGMA freelist_count;", cancellationToken);

        long pagesBytes = pageSize * pageCount;
        long freeBytes = pageSize * freePages;
        long bytesBefore = DiskBytes(connection.DataSource, pagesBytes);

        if (freePages == 0 || freeBytes < minFreeBytes || freeBytes < pagesBytes * minFreeRatio)
            return new(false, freeBytes, bytesBefore, bytesBefore);

        long autoVacuum = await ScalarAsync(connection, "PRAGMA auto_vacuum;", cancellationToken);
        if (autoVacuum == IncrementalAutoVacuum)
        {
            await ExecuteAsync(connection, "PRAGMA incremental_vacuum;", cancellationToken);
        }
        else
        {
            // One full rebuild converts the file; every later pass frees pages without one.
            await ExecuteAsync(connection, "PRAGMA auto_vacuum=INCREMENTAL;", cancellationToken);
            await ExecuteAsync(connection, "VACUUM;", cancellationToken);
        }

        bool returned = await ReturnSpaceToFileSystemAsync(
            connection,
            checkpointWait,
            cancellationToken
        );

        long pageCountAfter = await ScalarAsync(
            connection,
            "PRAGMA page_count;",
            cancellationToken
        );
        long bytesAfter = DiskBytes(connection.DataSource, pageSize * pageCountAfter);
        return new(returned, freeBytes, bytesBefore, bytesAfter, !returned);
    }

    private static long DiskBytes(string? databasePath, long fallback)
    {
        if (string.IsNullOrEmpty(databasePath) || !File.Exists(databasePath))
            return fallback;

        long bytes = new FileInfo(databasePath).Length;
        FileInfo wal = new(databasePath + "-wal");
        return wal.Exists ? bytes + wal.Length : bytes;
    }

    private static async Task<bool> ReturnSpaceToFileSystemAsync(
        DbConnection connection,
        TimeSpan checkpointWait,
        CancellationToken cancellationToken
    )
    {
        DateTime deadline = DateTime.UtcNow + checkpointWait;
        while (true)
        {
            (long walFrames, long backfilled) = await CheckpointAsync(
                connection,
                "PASSIVE",
                cancellationToken
            );

            // -1 frames: the file is not in WAL mode and the rebuild already shrank it.
            if (walFrames < 0 || backfilled >= walFrames)
            {
                await RestartWalAsync(connection, cancellationToken);
                return true;
            }

            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(CheckpointPollInterval, cancellationToken);
        }
    }

    // Best effort: it also empties the WAL file, but a busy reader only means the next checkpoint does it.
    private static async Task RestartWalAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        long previous = await ScalarAsync(connection, "PRAGMA busy_timeout;", cancellationToken);
        await ExecuteAsync(
            connection,
            $"PRAGMA busy_timeout={WalRestartBusyTimeoutMilliseconds};",
            cancellationToken
        );
        try
        {
            await CheckpointAsync(connection, "TRUNCATE", cancellationToken);
        }
        finally
        {
            await ExecuteAsync(connection, $"PRAGMA busy_timeout={previous};", cancellationToken);
        }
    }

    private static async Task<(long WalFrames, long Backfilled)> CheckpointAsync(
        DbConnection connection,
        string mode,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA wal_checkpoint({mode});";
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return (-1, -1);

        return (reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> ScalarAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        // A rebuild of a multi-gigabyte file outlasts the 30 s default command timeout.
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
