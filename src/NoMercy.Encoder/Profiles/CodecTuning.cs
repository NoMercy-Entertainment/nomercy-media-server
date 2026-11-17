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

using NoMercy.Encoder.Codecs;

namespace NoMercy.Encoder.Profiles;

/// <summary>
/// Turns an <see cref="EncodingQuality"/> tier into per-codec settings. CRF 22
/// does not mean the same thing to x264, x265, SVT-AV1 and libvpx, so presets
/// pick a tier and this decides what that costs on each encoder.
/// </summary>
public static class CodecTunings
{
    public record CodecTuning(
        int Crf,
        string Preset,
        CodecProfile Profile,
        int BitDepth,
        Dictionary<string, string>? ExtraArgs = null
    );

    public static CodecTuning For(VideoCodecType codec, EncodingQuality quality) =>
        codec switch
        {
            VideoCodecType.H264 => ForH264(quality),
            VideoCodecType.H265 => ForH265(quality),
            VideoCodecType.Av1 => ForAv1(quality),
            VideoCodecType.Vp9 => ForVp9(quality),
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
        };

    private static CodecTuning ForH264(EncodingQuality q) =>
        q switch
        {
            EncodingQuality.Archive => new(
                18,
                "veryslow",
                CodecProfile.High,
                8,
                new()
                {
                    ["x264.aq-mode"] = "3",
                    ["x264.rc-lookahead"] = "60",
                    ["x264.ref"] = "5",
                    ["x264.psy-rd"] = "1.0,0.15",
                }
            ),
            EncodingQuality.Ultra => new(
                18,
                "slower",
                CodecProfile.High,
                8,
                new() { ["x264.aq-mode"] = "3" }
            ),
            EncodingQuality.VeryHigh => new(20, "slow", CodecProfile.High, 8),
            EncodingQuality.High => new(22, "slow", CodecProfile.High, 8),
            EncodingQuality.Balanced => new(23, "medium", CodecProfile.High, 8),
            EncodingQuality.Streaming => new(24, "medium", CodecProfile.High, 8),
            EncodingQuality.Fast => new(26, "fast", CodecProfile.Main, 8),
            EncodingQuality.Preview => new(28, "veryfast", CodecProfile.Baseline, 8),
            _ => new(23, "medium", CodecProfile.High, 8),
        };

    private static CodecTuning ForH265(EncodingQuality q) =>
        q switch
        {
            EncodingQuality.Archive => new(
                18,
                "slower",
                CodecProfile.Main10,
                10,
                new() { ["x265.rdoq-level"] = "2", ["x265.aq-mode"] = "3" }
            ),
            EncodingQuality.Ultra => new(18, "slow", CodecProfile.Main10, 10),
            EncodingQuality.VeryHigh => new(20, "slow", CodecProfile.Main10, 10),
            EncodingQuality.High => new(22, "slow", CodecProfile.Main10, 10),
            EncodingQuality.Balanced => new(24, "medium", CodecProfile.Main10, 10),
            EncodingQuality.Streaming => new(24, "medium", CodecProfile.Main10, 10),
            EncodingQuality.Fast => new(26, "fast", CodecProfile.Main10, 10),
            EncodingQuality.Preview => new(30, "faster", CodecProfile.Main10, 10),
            _ => new(24, "medium", CodecProfile.Main10, 10),
        };

    private static CodecTuning ForAv1(EncodingQuality q) =>
        q switch
        {
            EncodingQuality.Archive => new(
                18,
                "2",
                CodecProfile.Main10,
                10,
                new() { ["svtav1.tune"] = "0", ["svtav1.enable-qm"] = "1" }
            ),
            EncodingQuality.Ultra => new(22, "3", CodecProfile.Main10, 10),
            EncodingQuality.VeryHigh => new(26, "4", CodecProfile.Main10, 10),
            EncodingQuality.High => new(28, "4", CodecProfile.Main10, 10),
            EncodingQuality.Balanced => new(30, "5", CodecProfile.Main10, 10),
            EncodingQuality.Streaming => new(32, "6", CodecProfile.Main10, 10),
            EncodingQuality.Fast => new(34, "7", CodecProfile.Main10, 10),
            EncodingQuality.Preview => new(36, "8", CodecProfile.Main10, 10),
            _ => new(30, "5", CodecProfile.Main10, 10),
        };

    private static CodecTuning ForVp9(EncodingQuality q) =>
        q switch
        {
            EncodingQuality.Archive => new(
                30,
                "good",
                CodecProfile.Main,
                8,
                new()
                {
                    ["vp9.cpu-used"] = "0",
                    ["vp9.row-mt"] = "1",
                    ["vp9.lag-in-frames"] = "25",
                }
            ),
            EncodingQuality.Ultra => new(30, "good", CodecProfile.Main, 8),
            EncodingQuality.VeryHigh => new(32, "good", CodecProfile.Main, 8),
            EncodingQuality.High => new(34, "good", CodecProfile.Main, 8),
            EncodingQuality.Balanced => new(36, "good", CodecProfile.Main, 8),
            EncodingQuality.Streaming => new(38, "good", CodecProfile.Main, 8),
            EncodingQuality.Fast => new(40, "good", CodecProfile.Main, 8),
            EncodingQuality.Preview => new(44, "realtime", CodecProfile.Main, 8),
            _ => new(36, "good", CodecProfile.Main, 8),
        };
}
