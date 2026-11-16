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
/// Writes and publishes one backup set. It does not prune; the caller decides
/// when that is safe.
/// </summary>
public interface IBackupSetWriter
{
    /// <summary>
    /// Copies <paramref name="databases"/> (SQLite online backup, then
    /// <c>quick_check</c>) and every file under <paramref name="configRoot"/>
    /// into a hidden folder readable by the owner only, writes the manifest, and
    /// publishes the set with one rename. Throws, leaving nothing behind, when
    /// any copy fails or any database copy fails its check.
    /// </summary>
    Task<BackupManifest> WriteAsync(
        string reason,
        IReadOnlyList<BackupDatabaseFile> databases,
        string configRoot,
        CancellationToken cancellationToken
    );
}
