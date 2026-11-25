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

    /// <summary>Prunes now, the way every backup does after it is written.</summary>
    Task<BackupPruneResult> PruneAsync(CancellationToken cancellationToken);

    /// <summary>Prunes now; <paramref name="includeHandMadeCopies"/> takes hand-made copies for this run only.</summary>
    Task<BackupPruneResult> PruneAsync(
        bool includeHandMadeCopies,
        CancellationToken cancellationToken
    );

    /// <summary>The published sets, newest first.</summary>
    Task<IReadOnlyList<BackupSetSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Backs up the current state, then writes the request that the next start
    /// restores <paramref name="backupId"/>, naming that safety set. Both happen
    /// under the gate every backup and prune takes, so neither set can be pruned
    /// in between. The caller verifies the set first.
    /// </summary>
    Task<RestoreScheduleResult> RequestRestoreAsync(
        string backupId,
        CancellationToken cancellationToken
    );

    /// <summary>Withdraws the waiting restore. False when there was none.</summary>
    Task<bool> CancelRestoreAsync(CancellationToken cancellationToken);
}
