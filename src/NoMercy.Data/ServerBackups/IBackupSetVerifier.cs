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
/// Proves a backup set is whole and usable before anything is restored from it.
/// </summary>
public interface IBackupSetVerifier
{
    /// <summary>
    /// Checks the set in <paramref name="setFolder"/>: the manifest reads and names
    /// <paramref name="expectedId"/>; it holds exactly the databases this server
    /// runs; every file is present with its recorded size and SHA-256; and no
    /// database carries a migration this server does not know, which would mean it
    /// comes from a newer server. An older schema is fine: it is migrated forward.
    /// </summary>
    Task<BackupVerification> VerifyAsync(
        string setFolder,
        string expectedId,
        CancellationToken cancellationToken
    );
}
