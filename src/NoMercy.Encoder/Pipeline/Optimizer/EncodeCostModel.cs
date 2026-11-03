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
using NoMercy.Encoder.Hardware;

namespace NoMercy.Encoder.Pipeline.Optimizer;

public class EncodeCostModel(ISpeedIndexProvider speedIndexProvider) : IEncodeCostModel
{
    // Codec encode-effort multipliers relative to H.264 (heavier = pricier per pixel).
    private const double H264Effort = 1.0;
    private const double H265Effort = 1.6;
    private const double Av1Effort = 2.4;
    private const double Vp9Effort = 1.8;

    // Decode + tonemap weights relative to one full-res H.264 encode-pixel pass.
    private const double DecodeWeight = 0.3;
    private const double TonemapWeight = 0.5;

    public double RungCost(int width, int height, VideoCodecType codec, string encoder, int passes)
    {
        double pixels = (double)width * height;
        double effort = EffortFor(codec);
        double speed = speedIndexProvider.Current.GetSpeedMultiplier(codec, encoder, width, null);
        // Unknown speed (0) → treat as 1.0 realtime baseline.
        double divisor = speed > 0 ? speed : 1.0;
        return pixels * effort * passes / divisor / 1_000_000.0;
    }

    public double TotalCost(int sourceWidth, int sourceHeight, bool sourceIsHdr, CostRung[] rungs)
    {
        double sourcePixels = (double)sourceWidth * sourceHeight / 1_000_000.0;
        double total = sourcePixels * DecodeWeight;
        if (sourceIsHdr)
            total += sourcePixels * TonemapWeight;

        foreach (CostRung rung in rungs)
            total += RungCost(rung.Width, rung.Height, rung.Codec, rung.Encoder, rung.Passes);

        return total;
    }

    private static double EffortFor(VideoCodecType codec) =>
        codec switch
        {
            VideoCodecType.H265 => H265Effort,
            VideoCodecType.Av1 => Av1Effort,
            VideoCodecType.Vp9 => Vp9Effort,
            _ => H264Effort,
        };
}
