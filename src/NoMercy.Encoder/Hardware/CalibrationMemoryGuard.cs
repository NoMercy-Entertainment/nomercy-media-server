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

namespace NoMercy.Encoder.Hardware;

/// <summary>
/// Decides whether a software calibration probe fits in the memory free right
/// now. Estimates are measured peak working sets of the testsrc probes at up to 16 threads.
/// </summary>
internal static class CalibrationMemoryGuard
{
    // Free memory required on top of the estimate: 25 percent.
    private const double SafetyMargin = 1.25;

    // Measured 4K peaks: libx264 2.5-4.8 GB, libx265 2.8-5.0 GB, SVT-AV1 7-9 GB
    // at any thread count. Below this much free memory no 4K probe runs.
    internal const long SoftwareUhdMinAvailableMb = 8_192;

    private const int UhdWidth = 3840;

    private sealed record MemoryModel(double BaseMb, double PerMegapixelMb);

    // peak MB = BaseMb + PerMegapixelMb x megapixels, fitted to the measured peaks.
    private static readonly Dictionary<string, MemoryModel> Models = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["libx264"] = new(150, 600),
        ["libx265"] = new(450, 600),
        ["libsvtav1"] = new(600, 1_100),
        ["libaom-av1"] = new(100, 620),
        ["librav1e"] = new(100, 620),
        ["libvpx-vp9"] = new(100, 240),
    };

    // An encoder without measurements is assumed to be as hungry as SVT-AV1.
    private static readonly MemoryModel UnknownEncoder = new(600, 1_100);

    internal static long EstimatePeakMb(string encoderName, int width, int height)
    {
        MemoryModel model = Models.GetValueOrDefault(encoderName, UnknownEncoder);
        double megapixels = (double)width * height / 1_000_000.0;
        return (long)Math.Ceiling(model.BaseMb + model.PerMegapixelMb * megapixels);
    }

    /// <summary>
    /// Returns why the probe must be skipped, or null when it fits. An
    /// availableMb of zero or less means the reading is unknown, so the probe runs.
    /// </summary>
    internal static string? SkipReason(string encoderName, int width, int height, long availableMb)
    {
        if (availableMb <= 0)
            return null;

        if (width >= UhdWidth && availableMb < SoftwareUhdMinAvailableMb)
            return $"4K software probes need at least {SoftwareUhdMinAvailableMb} MB free";

        long neededMb = (long)
            Math.Ceiling(EstimatePeakMb(encoderName, width, height) * SafetyMargin);
        return availableMb < neededMb ? $"it needs about {neededMb} MB free" : null;
    }
}
