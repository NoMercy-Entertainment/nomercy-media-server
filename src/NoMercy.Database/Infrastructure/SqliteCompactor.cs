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

    public async Task<SqliteCompactionResult> CompactIfWorthItAsync(
        DbConnection connection,
        double minFreeRatio,
        long minFreeBytes,
        CancellationToken cancellationToken = default
    )
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        long pageSize = await ScalarAsync(connection, "PRAGMA page_size;", cancellationToken);
        long pageCount = await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken);
        long freePages = await ScalarAsync(connection, "PRAGMA freelist_count;", cancellationToken);

        long bytesBefore = pageSize * pageCount;
        long freeBytes = pageSize * freePages;

        if (freePages == 0 || freeBytes < minFreeBytes || freeBytes < bytesBefore * minFreeRatio)
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

        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);

        long pageCountAfter = await ScalarAsync(
            connection,
            "PRAGMA page_count;",
            cancellationToken
        );
        return new(true, freeBytes, bytesBefore, pageSize * pageCountAfter);
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
