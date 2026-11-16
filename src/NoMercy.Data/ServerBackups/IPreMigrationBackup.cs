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
/// The safety copy of one database that is taken right before EF migrations
/// change it. Never fatal: a failure is logged and the migration goes ahead.
/// </summary>
public interface IPreMigrationBackup
{
    /// <summary>
    /// Copies <paramref name="databasePath"/> to
    /// <c>&lt;backups&gt;/&lt;name&gt;.&lt;yyyyMMddHHmmss&gt;.db</c> when
    /// <paramref name="pendingMigrationCount"/> is above zero, then keeps only the
    /// newest copies of that database. True when a copy was written.
    /// </summary>
    bool BackupBeforeMigration(string databasePath, int pendingMigrationCount);
}
