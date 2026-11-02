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

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// A name where our answer differs from the source's and ours is defensible.
/// <c>Ours</c> is pinned: when the answer changes, the entry stops matching
/// and the name fails again.
/// </summary>
internal sealed record KnownDifference(
    string Name,
    string Field,
    string Ours,
    string Theirs,
    string Reason
);
