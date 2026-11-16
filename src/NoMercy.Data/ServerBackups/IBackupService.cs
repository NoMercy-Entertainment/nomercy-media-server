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
/// Complete backup sets: every database plus the settings folder, written while
/// the server runs, described by a manifest with a checksum per file.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Writes a new set and prunes the oldest beyond the retain count. The set is
    /// built in a hidden folder and published by one rename, so a list never
    /// shows half a set. Throws when any part cannot be copied; nothing is left.
    /// </summary>
    Task<BackupSetSummary> CreateAsync(string reason, CancellationToken cancellationToken);

    /// <summary>The published sets, newest first.</summary>
    Task<IReadOnlyList<BackupSetSummary>> ListAsync(CancellationToken cancellationToken);
}
