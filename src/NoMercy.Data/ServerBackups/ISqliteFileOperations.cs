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
/// The SQLite work a backup needs on whole database files. Every connection is
/// unpooled, so no file handle outlives the call.
/// </summary>
public interface ISqliteFileOperations
{
    /// <summary>
    /// Copies a database that may be in use through SQLite's online-backup API,
    /// which sees rows still in the <c>-wal</c> file that a file copy would miss.
    /// The copy is then sealed (see <see cref="Seal"/>).
    /// </summary>
    void CopyOnline(string sourcePath, string destinationPath);

    /// <summary>
    /// Folds any <c>-wal</c> content into the database file and switches it to a
    /// rollback journal, so the file stands alone: no <c>-wal</c> or <c>-shm</c>
    /// file is left next to it.
    /// </summary>
    void Seal(string databasePath);

    /// <summary>
    /// The EF Core migrations recorded as applied, oldest first. Empty when the
    /// database has no migration history. Opens the file read-only.
    /// </summary>
    IReadOnlyList<string> ReadAppliedMigrations(string databasePath);

    /// <summary>
    /// Null when <c>PRAGMA quick_check</c> finds nothing wrong; otherwise what it
    /// (or SQLite, for a file it cannot open) reports, at most five findings.
    /// </summary>
    string? FindDamage(string databasePath);

    /// <summary>
    /// Closes every pooled SQLite connection in this process, so no handle holds a
    /// database file while it is renamed.
    /// </summary>
    void ReleaseAllConnections();
}
