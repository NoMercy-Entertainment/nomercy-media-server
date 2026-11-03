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

/// <summary>Converts between ffprobe transfer names, <see cref="VideoRange"/> and HLS.</summary>
public static class VideoRangeMapping
{
    private const string HlgTransfer = "arib-std-b67";

    /// <summary>
    /// The range of an output that keeps its source's HDR. ffprobe
    /// <c>arib-std-b67</c> is HLG; anything else, including an unknown or
    /// missing transfer, is PQ, the HDR transfer the encoder signals by default.
    /// </summary>
    public static VideoRange ForHdrTransfer(string? colorTransfer) =>
        string.Equals(colorTransfer, HlgTransfer, StringComparison.OrdinalIgnoreCase)
            ? VideoRange.Hlg
            : VideoRange.Pq;

    /// <summary>The value of the HLS <c>VIDEO-RANGE</c> attribute: SDR, PQ or HLG.</summary>
    public static string ToHlsVideoRange(this VideoRange range) =>
        range switch
        {
            VideoRange.Pq => "PQ",
            VideoRange.Hlg => "HLG",
            _ => "SDR",
        };
}
