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

namespace NoMercy.Database;

/// <summary>
/// Restarts a SQLite WAL that grew past a limit. SQLite's automatic checkpoint cannot
/// restart the WAL while any reader is active, so under constant reads it only grows.
/// </summary>
public sealed class SqliteWalCheckpointer
{
    // How long the restart waits for running reads to end. It holds the write lock
    // meanwhile, so this stays below the writers' own 5 s busy_timeout.
    private const int BusyTimeoutMilliseconds = 2000;

    public async Task<WalCheckpointResult> TruncateIfOverLimitAsync(
        string databasePath,
        long walLimitBytes,
        CancellationToken cancellationToken = default
    )
    {
        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        await using SqliteConnection connection = new(connectionString.ToString());
        await connection.OpenAsync(cancellationToken);

        await using (SqliteCommand busyTimeout = connection.CreateCommand())
        {
            busyTimeout.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
            await busyTimeout.ExecuteNonQueryAsync(cancellationToken);
        }

        long pageSize;
        await using (SqliteCommand pageSizeCommand = connection.CreateCommand())
        {
            pageSizeCommand.CommandText = "PRAGMA page_size;";
            pageSize = Convert.ToInt64(await pageSizeCommand.ExecuteScalarAsync(cancellationToken));
        }

        (_, long walFrames) = await CheckpointAsync(connection, "PASSIVE", cancellationToken);
        if (walFrames * pageSize <= walLimitBytes)
            return new(walFrames, OverLimit: false, Truncated: false);

        (bool blocked, _) = await CheckpointAsync(connection, "TRUNCATE", cancellationToken);
        return new(walFrames, OverLimit: true, Truncated: !blocked);
    }

    private static async Task<(bool Blocked, long WalFrames)> CheckpointAsync(
        SqliteConnection connection,
        string mode,
        CancellationToken cancellationToken
    )
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA wal_checkpoint({mode});";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return (true, -1);

        return (reader.GetInt64(0) != 0, reader.GetInt64(1));
    }
}
