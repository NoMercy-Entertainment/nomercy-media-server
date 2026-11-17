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
/// Stage 3 output: a stretch of an episode whose audio most partners hold in the
/// same zone.
/// </summary>
/// <param name="Position">Start as a share of the duration (intros only; 0 for outros).</param>
/// <param name="Consistent">Most partners holding this audio in the same zone, at any bin.</param>
/// <param name="Support">Partners sharing any of this audio, wherever they hold it.</param>
/// <param name="Coverage">Median share of the stretch each same-zone partner covers.</param>
public sealed record ThemeRegion(
    ThemeKind Kind,
    double Start,
    double End,
    double Position,
    int Consistent,
    int Support,
    double Coverage
);
