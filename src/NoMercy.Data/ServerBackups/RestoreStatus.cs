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

public enum RestoreStatus
{
    /// <summary>The backup is in place.</summary>
    Applied,

    /// <summary>Nothing was changed: the backup failed a check before any live file moved.</summary>
    Refused,

    /// <summary>The swap failed or was interrupted, and every live file is back as it was.</summary>
    RolledBack,

    /// <summary>
    /// The swap failed and putting the old files back failed too. The journal is
    /// kept, the server refuses to start, and the next start tries again.
    /// </summary>
    RollbackIncomplete,
}
