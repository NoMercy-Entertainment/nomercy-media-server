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
/// A run of chromaprint hashes that starts <paramref name="OffsetSeconds"/> into
/// the episode. The server always fingerprints the whole track, so it has one
/// part at offset 0 (#344: a tail window made outro times negative, and openings
/// sit as late as 14:31). Recorded research inputs for two single-file seasons
/// hold a head and a tail part, which is why this is a list.
/// </summary>
public sealed record FingerprintPart(double OffsetSeconds, uint[] Hashes);
