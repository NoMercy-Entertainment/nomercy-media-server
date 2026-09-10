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

namespace NoMercy.Database.Models.Music;

/// <summary>
/// What part of the track a stem file covers. The retention query reads
/// this, never the millisecond window, so "do we have what the policy wants"
/// is one equality rather than arithmetic on durations.
/// </summary>
public enum StemCoverage
{
    Full = 0,

    /// <summary>The first 20 % of the track: where a mix-in lands.</summary>
    MixIn = 1,

    /// <summary>The last 25 % of the track: where a mix-out lands.</summary>
    MixOut = 2,
}
