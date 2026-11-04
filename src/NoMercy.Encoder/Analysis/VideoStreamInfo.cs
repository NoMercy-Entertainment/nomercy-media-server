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

namespace NoMercy.Encoder.Analysis;

public record VideoStreamInfo(
    int Index,
    string Codec,
    int Width,
    int Height,
    double FrameRate,
    int BitDepth,
    string PixelFormat,
    string? ColorPrimaries,
    string? ColorTransfer,
    string? ColorSpace,
    bool IsDefault,
    long BitRateKbps,
    double? AverageFrameRate = null,
    double? RealFrameRate = null,
    int Rotation = 0,
    string? FieldOrder = null,
    string? SampleAspectRatio = null
)
{
    public (int Width, int Height) GetDisplayDimensions() =>
        Math.Abs(Rotation % 180) == 90 ? (Height, Width) : (Width, Height);

    // ffprobe reports sample_aspect_ratio as "num:den". Anamorphic content has
    // non-square pixels (SAR != 1:1) that MUST be squared on transcode or the
    // picture comes out stretched/squished. "0:1" is ffprobe's "unknown" and is
    // treated as square, as is an absent tag.
    public bool IsAnamorphic
    {
        get
        {
            if (SampleAspectRatio is null)
                return false;
            string[] parts = SampleAspectRatio.Split(':');
            if (
                parts.Length != 2
                || !int.TryParse(parts[0], out int num)
                || !int.TryParse(parts[1], out int den)
            )
                return false;
            return num > 0 && den > 0 && num != den;
        }
    }

    private static readonly HashSet<string> HdrTransfers = ["smpte2084", "arib-std-b67"];

    // ffprobe field_order values for interlaced content. "progressive"
    // (or an absent tag) is not interlaced; the four interlaced orders all
    // need a deinterlace before scale or the output combs.
    private static readonly HashSet<string> InterlacedFieldOrders = ["tt", "bb", "tb", "bt"];

    public bool IsInterlaced =>
        FieldOrder is not null && InterlacedFieldOrders.Contains(FieldOrder);

    public bool IsHdr =>
        ColorTransfer is not null
        && HdrTransfers.Contains(ColorTransfer)
        && ColorPrimaries is "bt2020";

    // 1% spread between real and average frame-rate is the standard VFR
    // threshold ffmpeg / handbrake use. AnalyzeStage's decision-log emitter
    // and ProfileRuleValidator's SourceVariableFrameRate rule both consume
    // this property — keeping them in sync means a stream that triggers the
    // validation warning also surfaces in the decision log, and vice versa.
    public bool IsVariableFrameRate
    {
        get
        {
            if (!AverageFrameRate.HasValue || !RealFrameRate.HasValue)
                return false;
            double diff = Math.Abs(RealFrameRate.Value - AverageFrameRate.Value);
            return diff / Math.Max(RealFrameRate.Value, 1.0) > 0.01;
        }
    }
}
