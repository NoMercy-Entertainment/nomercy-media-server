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

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>
/// Audio this episode shares with one partner: <c>[Start, End)</c> here holds
/// the same sound as <c>[PartnerStart, PartnerEnd)</c> in episode <c>Partner</c>
/// (an index into the season).
/// </summary>
public readonly record struct PartnerHit(
    double Start,
    double End,
    int Partner,
    double PartnerStart,
    double PartnerEnd
);
