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
/// Accepts a restore while the server runs. It does not touch a live file: it
/// verifies the set, backs up the current state, and leaves a request that the
/// next start applies before anything opens the databases.
/// </summary>
public interface IRestoreScheduler
{
    Task<RestoreScheduleResult> ScheduleAsync(string backupId, CancellationToken cancellationToken);

    /// <summary>Withdraws a waiting request. False when there was none.</summary>
    Task<bool> CancelAsync(CancellationToken cancellationToken);

    Task<PendingRestore?> GetPendingAsync(CancellationToken cancellationToken);

    /// <summary>How the last restore ended, or null when none was ever tried.</summary>
    Task<RestoreResult?> GetLastResultAsync(CancellationToken cancellationToken);
}
