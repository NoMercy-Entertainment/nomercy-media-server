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

/// <summary>Stage 1 for one episode: the whole-track fingerprint and its silences.</summary>
public sealed record AudioSignals(
    double DurationSeconds,
    uint[] Hashes,
    IReadOnlyList<TimeRange> Silences
);
