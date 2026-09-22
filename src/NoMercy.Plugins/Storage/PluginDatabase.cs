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

using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Storage;

/// <summary>
/// One SQLite file in the plugin's private folder.
/// <para>
/// The host opens it, applies the plugin's migrations in order and remembers
/// how far it got, so no plugin ships its own schema-version table again. The
/// torrent plugin hand-rolled 2300 lines of ADO to get here.
/// </para>
/// <para>
/// The file is the plugin's alone. It is not the server's library database
/// and nothing reachable from here can open that one.
/// </para>
/// </summary>
public class PluginDatabase : IPluginDatabase
{
    /// <summary>Where the host records how many of a plugin's migrations ran.</summary>
    public const string VersionTable = "__nomercy_plugin_schema";

    private readonly SqliteConnection _connection;

    private PluginDatabase(SqliteConnection connection) => _connection = connection;

    public static async Task<PluginDatabase> OpenAsync(string path, CancellationToken ct = default)
    {
        if (Path.GetDirectoryName(path) is { } folder)
            Directory.CreateDirectory(folder);

        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString()
        );

        await connection.OpenAsync(ct);

        PluginDatabase database = new(connection);

        await database.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS {VersionTable} (applied INTEGER NOT NULL)",
            ct: ct
        );

        return database;
    }

    public async Task MigrateAsync(IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        int applied = Convert.ToInt32(
            await ScalarAsync($"SELECT MAX(applied) FROM {VersionTable}", ct: ct) ?? 0
        );

        for (int index = applied; index < statements.Count; index++)
        {
            // One transaction each, so a list of five where the fourth is
            // wrong leaves three applied and says so, rather than leaving a
            // half-built table nobody can name the state of.
            await using SqliteTransaction transaction = (SqliteTransaction)
                await _connection.BeginTransactionAsync(ct);

            await ExecuteAsync(statements[index], null, ct, transaction);
            await ExecuteAsync(
                $"INSERT INTO {VersionTable} (applied) VALUES ($applied)",
                new Dictionary<string, object?> { ["$applied"] = index + 1 },
                ct,
                transaction
            );

            await transaction.CommitAsync(ct);
        }
    }

    public Task<int> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default
    ) => ExecuteAsync(sql, parameters, ct, transaction: null);

    private async Task<int> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct,
        SqliteTransaction? transaction
    )
    {
        await using SqliteCommand command = Command(sql, parameters, transaction);

        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default
    )
    {
        List<IReadOnlyDictionary<string, object?>> rows = [];

        await foreach (
            IReadOnlyDictionary<string, object?> row in QueryStreamAsync(sql, parameters, ct)
        )
            rows.Add(row);

        return rows;
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryStreamAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await using SqliteCommand command = Command(sql, parameters, null);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            Dictionary<string, object?> row = [];

            for (int column = 0; column < reader.FieldCount; column++)
                row[reader.GetName(column)] = reader.IsDBNull(column)
                    ? null
                    : reader.GetValue(column);

            yield return row;
        }
    }

    public async Task<object?> ScalarAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default
    )
    {
        await using SqliteCommand command = Command(sql, parameters, null);
        object? value = await command.ExecuteScalarAsync(ct);

        return value is DBNull ? null : value;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Bound, never interpolated. A plugin concatenating a value into its own
    /// SQL can only injure its own file, but the bound path is the one that
    /// survives a track title containing an apostrophe.
    /// </summary>
    private SqliteCommand Command(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        SqliteTransaction? transaction
    )
    {
        SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        foreach ((string name, object? value) in parameters ?? new Dictionary<string, object?>())
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        return command;
    }
}
