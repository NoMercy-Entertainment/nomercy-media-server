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

namespace NoMercy.Encoder.Pipeline.Resume;

/// <summary>
/// Reads the presentation time of the first packet of an encoded segment.
/// </summary>
public interface ISegmentProber
{
    /// <returns>Seconds, or null when the file cannot be probed.</returns>
    Task<double?> FirstPacketPtsAsync(string segmentPath, bool video, CancellationToken ct);
}
