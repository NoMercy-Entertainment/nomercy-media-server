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

/// <summary>
/// Real SQLite work, except that the integrity check reports damage for any
/// test database whose marker row reads <see cref="CorruptMarker"/>. Real
/// corruption that the online backup still copies but quick_check flags is
/// hard to make on purpose; this keeps the test deterministic.
/// </summary>
public sealed class MarkerDamageSqlite : ISqliteFileOperations
{
    public const string CorruptMarker = "corrupt";
    public const string Damage = "*** in database main *** Page 2: fake damage";

    private readonly SqliteFileOperations _inner = new();

    public void CopyOnline(string sourcePath, string destinationPath) =>
        _inner.CopyOnline(sourcePath, destinationPath);

    public void Seal(string databasePath) => _inner.Seal(databasePath);

    public IReadOnlyList<string> ReadAppliedMigrations(string databasePath) =>
        _inner.ReadAppliedMigrations(databasePath);

    public string? FindDamage(string databasePath)
    {
        using SqliteConnection connection = new(
            $"Data Source={databasePath};Pooling=False;Mode=ReadOnly"
        );
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'marker'";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
            return _inner.FindDamage(databasePath);

        command.CommandText = "SELECT content FROM marker";
        return command.ExecuteScalar() as string == CorruptMarker
            ? Damage
            : _inner.FindDamage(databasePath);
    }

    public void ReleaseAllConnections() => _inner.ReleaseAllConnections();
}
