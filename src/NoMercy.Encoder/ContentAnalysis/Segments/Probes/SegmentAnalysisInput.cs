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

namespace NoMercy.Encoder.ContentAnalysis.Segments.Probes;

/// <summary>One episode (or a movie) to analyse, in season order.</summary>
/// <param name="Key">Stable id the caller maps results back with (an episode id).</param>
public sealed record SegmentAnalysisInput(string Key, SegmentMediaSource Source);
