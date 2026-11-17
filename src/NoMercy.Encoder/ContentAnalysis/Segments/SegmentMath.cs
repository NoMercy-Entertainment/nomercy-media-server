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

/// <summary>Small numeric helpers shared by the stages.</summary>
internal static class SegmentMath
{
    /// <summary>Rounds half to even, the way the measured prototype rounded its times.</summary>
    public static double Round(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.ToEven);

    public static double Overlap(double a0, double a1, double b0, double b1) =>
        Math.Max(0.0, Math.Min(a1, b1) - Math.Max(a0, b0));

    /// <summary>Sorted runs merged where one starts within <paramref name="gap"/> of the previous end.</summary>
    public static List<TimeRange> Merge(IEnumerable<TimeRange> runs, double gap)
    {
        List<TimeRange> merged = [];
        foreach (TimeRange run in runs.OrderBy(r => r.Start).ThenBy(r => r.End))
        {
            if (merged.Count > 0 && run.Start <= merged[^1].End + gap)
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, run.End) };
            else
                merged.Add(run);
        }

        return merged;
    }

    /// <summary>Partners needed in the same zone: 1 with fewer than 3, else max(2, 40 %).</summary>
    public static int NeededPartners(int partners) =>
        partners < 3 ? 1 : Math.Max(2, (int)Math.Ceiling(SegmentRules.NeedFraction * partners));
}
