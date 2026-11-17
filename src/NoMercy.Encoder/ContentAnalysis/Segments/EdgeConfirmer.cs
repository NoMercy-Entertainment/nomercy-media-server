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

/// <summary>
/// Stage 4: moves a candidate edge onto the picture.
/// <para>
/// First the edge comes off the silence the fingerprint absorbed (<see cref="Snap"/>),
/// which is where the picture is probed. Then <see cref="Confirm"/> checks, in
/// order: a scene cut within ±2 s, black within ±2 s, silence at the edge, and a
/// cut or black up to 5 s inward (music leads the picture into a theme: Rick and
/// Morty's song starts 2.5–4 s under the end of the cold open). The edge snaps to
/// what it found. File start and end count as edges.
/// </para>
/// </summary>
public static class EdgeConfirmer
{
    /// <summary>The edge moved off a silence within 4 s, and whether that happened.</summary>
    public static (double Time, bool OnSilence) Snap(
        IReadOnlyList<TimeRange> silences,
        double t,
        bool isStart
    )
    {
        double? best = null;
        foreach (TimeRange silence in silences)
        {
            if (
                t < silence.Start - SegmentRules.EdgeSnapSeconds
                || t > silence.End + SegmentRules.EdgeSnapSeconds
            )
                continue;

            double candidate = isStart ? silence.End : silence.Start;
            if (best is null || Math.Abs(candidate - t) < Math.Abs(best.Value - t))
                best = candidate;
        }

        return best is null ? (t, false) : (best.Value, true);
    }

    /// <summary>True when plain black lies within 1 s of the edge (reported, not decided on).</summary>
    public static bool NearBlack(BlackRuns? black, double t) =>
        black is not null && black.Strict.Any(b => b.Start - 1.0 <= t && t <= b.End + 1.0);

    /// <summary>The edge refined onto the picture and what confirmed it.</summary>
    public static (double Time, EdgeEvidence Evidence) Confirm(
        double duration,
        IReadOnlyList<TimeRange> silences,
        EdgeProbeSet? probes,
        double t,
        bool isStart
    )
    {
        if (t < SegmentRules.FileEdgeSeconds || t > duration - SegmentRules.FileEdgeSeconds)
            return (t, EdgeEvidence.File);

        EdgeProbe? probe = probes?.Nearest(t);
        if (probe is null)
            return (t, EdgeEvidence.Unprobed);

        double? cut = null;
        foreach (double c in probe.Cuts)
        {
            if (Math.Abs(c - t) > SegmentRules.EdgeToleranceSeconds)
                continue;
            if (cut is null || Math.Abs(c - t) < Math.Abs(cut.Value - t))
                cut = c;
        }

        if (cut is not null)
            return (cut.Value, EdgeEvidence.Cut);

        foreach (TimeRange black in probe.Black)
        {
            if (
                black.Start - SegmentRules.EdgeToleranceSeconds <= t
                && t <= black.End + SegmentRules.EdgeToleranceSeconds
            )
                return (isStart ? black.End : black.Start, EdgeEvidence.Black);
        }

        if (silences.Any(s => s.Start - 0.3 <= t && t <= s.End + 0.3))
            return (t, EdgeEvidence.Silence);

        List<double> inward = [];
        foreach (double c in probe.Cuts)
        {
            double distance = isStart ? c - t : t - c;
            if (distance > 0 && distance <= SegmentRules.LeadSeconds)
                inward.Add(c);
        }

        foreach (TimeRange black in probe.Black)
        {
            if (isStart && black.End - t > 0 && black.End - t <= SegmentRules.LeadSeconds)
                inward.Add(black.End);
            if (!isStart && t - black.Start > 0 && t - black.Start <= SegmentRules.LeadSeconds)
                inward.Add(black.Start);
        }

        if (inward.Count > 0)
            return (isStart ? inward.Min() : inward.Max(), EdgeEvidence.Lead);

        return (t, EdgeEvidence.None);
    }
}
