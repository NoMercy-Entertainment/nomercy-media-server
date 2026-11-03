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

namespace NoMercy.Encoder.Output;

/// <summary>
/// The transfer characteristic of a video output, as the HLS master's
/// <c>VIDEO-RANGE</c> attribute names it. A player picks its transfer function
/// from this, so PQ (HDR10, ffprobe <c>smpte2084</c>) and HLG (ffprobe
/// <c>arib-std-b67</c>) must not be folded into one HDR flag.
/// </summary>
public enum VideoRange
{
    Sdr,
    Pq,
    Hlg,
}
