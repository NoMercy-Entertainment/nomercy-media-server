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

namespace NoMercy.Encoder.ContentAnalysis.Segments.Probes;

/// <summary>
/// Where the picture passes look, so they never decode a whole episode
/// (blackdetect over a whole 22 min episode costs 25.7 s; these windows 14.2 s).
/// </summary>
public static class SignalWindows
{
    /// <summary>
    /// blackdetect windows: ±1.5 s around every silence (theme edges sit there)
    /// plus the last 480 s (credits), merged where they come within 3 s.
    /// </summary>
    public static List<TimeRange> Black(double duration, IReadOnlyList<TimeRange> silences)
    {
        List<TimeRange> windows =
        [
            .. silences.Select(s => new TimeRange(
                Math.Max(0.0, s.Start - SegmentRules.SilenceWindowPadSeconds),
                Math.Min(duration, s.End + SegmentRules.SilenceWindowPadSeconds)
            )),
            new(Math.Max(0.0, duration - SegmentRules.VideoTailSeconds), duration),
        ];

        return SegmentMath.Merge(windows, 3.0);
    }

    /// <summary>
    /// Where OCR starts (it runs to the end). OCR is the expensive pass (60 s for
    /// 420 s of a 1080p episode), so it is targeted: from 60 s before the earliest
    /// dark block that can be credits, never more than the last 420 s; the whole
    /// 420 s only when no dark block exists (textured or overlay credits).
    /// </summary>
    public static double OcrStart(double duration, BlackRuns? black)
    {
        double tailStart = Math.Max(0.0, duration - SegmentRules.OcrTailSeconds);
        if (black is null)
            return tailStart;

        double limit = Math.Max(
            SegmentRules.CreditsTailSeconds,
            SegmentRules.CreditsTailFraction * duration
        );
        List<TimeRange> dark =
        [
            .. SegmentMath
                .Merge(
                    black.Loose.Where(r => r.Start > duration / 2),
                    SegmentRules.CreditsGapSeconds
                )
                .Where(r =>
                    r.Length >= SegmentRules.CreditsMinSeconds && duration - r.End <= limit
                ),
        ];
        if (dark.Count == 0)
            return tailStart;

        return Math.Max(tailStart, dark.Min(r => r.Start) - SegmentRules.OcrLeadSeconds);
    }
}
