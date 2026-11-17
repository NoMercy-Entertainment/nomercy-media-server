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

/// <summary>A stage-3 region with its edges moved off absorbed silence, waiting for picture evidence.</summary>
internal sealed record PendingCandidate(
    ThemeRegion Region,
    double ProbeStart,
    bool StartOnSilence,
    double ProbeEnd,
    bool EndOnSilence
);
