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

public enum RestoreScheduleStatus
{
    /// <summary>Verified, a safety backup made, and the request written for the next start.</summary>
    Scheduled,

    /// <summary>The id is not the name of a backup set.</summary>
    InvalidId,

    /// <summary>No backup set by that id.</summary>
    NotFound,

    /// <summary>The set failed verification; nothing was scheduled.</summary>
    Invalid,

    /// <summary>Another restore is already waiting for the next start.</summary>
    AlreadyPending,
}
