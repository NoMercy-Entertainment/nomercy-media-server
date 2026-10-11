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

namespace NoMercy.Data.Repositories;

/// <summary>
/// What the ordering of an anime group needs to know: no posters, only counts
/// and dates over the playable titles of the group.
/// </summary>
public record GroupRankRow(
    int Id,
    string Title,
    int PlayableCount,
    DateTime? NewestAddedAt,
    int WatchedCount,
    int Year = 0,
    int QuarterOrder = 0
);
