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

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// What this server's code knows about the schema of each database in a set:
/// the migrations it ships, and how to bring an older copy up to date.
/// </summary>
public interface IBackupDatabaseSchema
{
    /// <summary>The migration ids this server ships for a database, oldest first.</summary>
    IReadOnlyList<string> KnownMigrations(string databaseName);

    /// <summary>
    /// Applies this server's pending migrations to a staged copy, with the same
    /// repairs the boot migration makes. Throws when the copy is not fully up to
    /// date afterwards.
    /// </summary>
    Task MigrateAsync(
        string databaseName,
        string databasePath,
        CancellationToken cancellationToken
    );
}
