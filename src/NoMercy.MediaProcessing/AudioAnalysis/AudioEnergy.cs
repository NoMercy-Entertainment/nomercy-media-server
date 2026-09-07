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

namespace NoMercy.MediaProcessing.AudioAnalysis;

/// <summary>
/// Energy as a single 0..1 number.
/// <para>
/// This is a judgment, not a measurement. Nothing in the audio reports an
/// "energy", so the number is whatever formula is written here. Both inputs are
/// persisted alongside the output so a consumer that disagrees can recompute,
/// and changing the formula is an analyzer version bump rather than a silent
/// edit that quietly rewrites the meaning of an existing column.
/// </para>
/// </summary>
public static class AudioEnergy
{
    private const double QuietLufs = -30.0;
    private const double LoudLufs = -5.0;
    private const double DarkCentroidHz = 500.0;
    private const double BrightCentroidHz = 5000.0;
    private const double LoudnessWeight = 0.65;
    private const double BrightnessWeight = 0.35;

    /// <summary>
    /// Null when neither input is present. When only one is, that one carries
    /// the result — a partial answer beats discarding a usable signal.
    /// </summary>
    public static double? Estimate(double? integratedLufs, double? spectralCentroidHz)
    {
        double? loudness = Normalize(integratedLufs, QuietLufs, LoudLufs);
        double? brightness = Normalize(spectralCentroidHz, DarkCentroidHz, BrightCentroidHz);

        if (loudness is null && brightness is null)
        {
            return null;
        }

        if (loudness is null)
        {
            return brightness;
        }

        if (brightness is null)
        {
            return loudness;
        }

        double combined = LoudnessWeight * loudness.Value + BrightnessWeight * brightness.Value;

        return Math.Clamp(combined, 0.0, 1.0);
    }

    private static double? Normalize(double? value, double low, double high)
    {
        if (value is null)
        {
            return null;
        }

        double scaled = (value.Value - low) / (high - low);

        return Math.Clamp(scaled, 0.0, 1.0);
    }
}
