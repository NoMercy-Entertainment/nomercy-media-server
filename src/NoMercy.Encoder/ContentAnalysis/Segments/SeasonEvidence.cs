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
/// The picture evidence stages 4–5 read, by season index: edge probes and
/// thumbnails. Production fills it with exactly what
/// <see cref="SeasonCandidates"/> asks for; tests fill it from recorded probes.
/// </summary>
public sealed class SeasonEvidence
{
    public SeasonEvidence(int episodes)
    {
        EdgeProbes = new EdgeProbeSet?[episodes];
        Thumbnails = new ThumbnailStrip?[episodes];
    }

    public EdgeProbeSet?[] EdgeProbes { get; }

    public ThumbnailStrip?[] Thumbnails { get; }
}
