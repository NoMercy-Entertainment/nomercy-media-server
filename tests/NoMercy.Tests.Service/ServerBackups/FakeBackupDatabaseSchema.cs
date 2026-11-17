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
/// A schema that knows a fixed list of migrations for every database. "Migrating"
/// a copy stamps the missing ones into its history, the way EF Core would.
/// </summary>
public sealed class FakeBackupDatabaseSchema(params string[] knownMigrations)
    : IBackupDatabaseSchema
{
    /// <summary>When set, migrating that database throws.</summary>
    public string? FailFor { get; set; }

    public List<string> Migrated { get; } = [];

    public IReadOnlyList<string> KnownMigrations(string databaseName) => knownMigrations;

    public Task MigrateAsync(
        string databaseName,
        string databasePath,
        CancellationToken cancellationToken
    )
    {
        if (databaseName == FailFor)
            throw new InvalidOperationException($"Simulated migration failure on {databaseName}.");

        using SqliteConnection connection = new($"Data Source={databasePath};Pooling=False");
        connection.Open();
        foreach (string migration in knownMigrations)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ($id, '10.0.0')";
            command.Parameters.AddWithValue("$id", migration);
            command.ExecuteNonQuery();
        }

        Migrated.Add(databaseName);
        return Task.CompletedTask;
    }
}
