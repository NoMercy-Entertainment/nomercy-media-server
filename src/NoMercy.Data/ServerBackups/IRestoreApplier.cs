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
/// Puts a backup set in place, all or nothing. Runs at start, before anything
/// opens a database, so no connection holds a file while it is swapped.
/// </summary>
public interface IRestoreApplier
{
    /// <summary>
    /// First undoes a swap that a crash cut off (a journal left on disk), then
    /// applies the pending restore, if any. Null when there was nothing to do.
    /// A <see cref="RestoreStatus.RollbackIncomplete"/> result means the live
    /// files are not whole: the server must not start on them.
    /// </summary>
    /// <param name="previousServerStopped">
    /// False when the server process this one replaces has not exited: its open
    /// files make a swap unsafe, so the restore is refused and nothing moves.
    /// </param>
    Task<RestoreResult?> ApplyPendingAsync(
        bool previousServerStopped,
        CancellationToken cancellationToken
    );
}
