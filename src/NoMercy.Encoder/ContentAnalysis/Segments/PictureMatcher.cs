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
/// Stage 5: does the picture repeat along with the sound?
/// <para>
/// A real opening has the same sound and the same picture in every episode;
/// reused score has the same sound under different pictures (Squid Game: 0–1
/// matching partners; American Dad's intro 4/4, Hellsing 11–12/12). Frames are
/// aligned by the audio offset and match when their pixels correlate ≥ 0.85.
/// Not universal: Rick and Morty's opening shows different clips per episode,
/// and credits never match because the names differ.
/// </para>
/// </summary>
public static class PictureMatcher
{
    /// <summary>
    /// Partners whose picture matches this episode's over <c>[start, end]</c>,
    /// or null when no partner could be compared.
    /// </summary>
    /// <param name="thumbnails">Every episode's thumbnails by season index; null where none were taken.</param>
    public static PictureSupport? Support(
        int episode,
        double start,
        double end,
        IReadOnlyList<PartnerHit> hits,
        IReadOnlyList<ThumbnailStrip?> thumbnails
    )
    {
        ThumbnailStrip? own = thumbnails[episode];
        if (own is null)
            return null;

        // Partner -> (matched, checked), in first-seen order.
        List<int> order = [];
        Dictionary<int, (int Matched, int Checked)> tally = new();

        foreach (PartnerHit hit in hits)
        {
            double lo = Math.Max(start, hit.Start);
            double hi = Math.Min(end, hit.End);
            ThumbnailStrip? partner = thumbnails[hit.Partner];
            if (hi - lo < SegmentRules.MinComparedSeconds || partner is null)
                continue;

            if (!tally.ContainsKey(hit.Partner))
            {
                tally[hit.Partner] = (0, 0);
                order.Add(hit.Partner);
            }

            double offset = hit.PartnerStart - hit.Start;
            (int matched, int compared) = tally[hit.Partner];
            for (int k = LowerBound(own.Times, lo); k < own.Count && own.Times[k] <= hi; k++)
            {
                double want = own.Times[k] + offset;
                int j = Nearest(partner.Times, want);
                if (j < 0 || Math.Abs(partner.Times[j] - want) > SegmentRules.FrameAlignSeconds)
                    continue;

                compared++;
                if (FramesMatch(own.Frame(k), partner.Frame(j)))
                    matched++;
            }

            tally[hit.Partner] = (matched, compared);
        }

        List<(int Matched, int Checked)> checkedPartners =
        [
            .. order.Select(p => tally[p]).Where(v => v.Checked >= SegmentRules.MinComparedFrames),
        ];
        if (checkedPartners.Count == 0)
            return null;

        int matching = checkedPartners.Count(v =>
            (double)v.Matched / v.Checked >= SegmentRules.PartnerFrameFraction
        );
        return new(matching, checkedPartners.Count);
    }

    /// <summary>
    /// Two flat frames (e.g. both black) match by brightness; one flat frame never
    /// matches a detailed one; otherwise by Pearson correlation.
    /// </summary>
    internal static bool FramesMatch(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        (double meanA, double deviationA) = MeanAndDeviation(a);
        (double meanB, double deviationB) = MeanAndDeviation(b);
        bool flatA = deviationA < SegmentRules.FlatFrameDeviation;
        bool flatB = deviationB < SegmentRules.FlatFrameDeviation;
        if (flatA && flatB)
            return Math.Abs(meanA - meanB) < SegmentRules.FlatFrameBrightness;
        if (flatA || flatB)
            return false;

        double covariance = 0;
        for (int i = 0; i < a.Length; i++)
            covariance += (a[i] - meanA) * (b[i] - meanB);
        covariance /= a.Length;
        return covariance / (deviationA * deviationB) >= SegmentRules.FrameCorrelation;
    }

    private static (double Mean, double Deviation) MeanAndDeviation(ReadOnlySpan<byte> frame)
    {
        double sum = 0;
        foreach (byte value in frame)
            sum += value;
        double mean = sum / frame.Length;

        double squares = 0;
        foreach (byte value in frame)
            squares += (value - mean) * (value - mean);
        return (mean, Math.Sqrt(squares / frame.Length));
    }

    /// <summary>First index whose time is at least <paramref name="t"/>.</summary>
    private static int LowerBound(double[] times, double t)
    {
        int lo = 0;
        int hi = times.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] < t)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    /// <summary>Index of the time nearest <paramref name="t"/> (the earlier on a tie), or -1.</summary>
    private static int Nearest(double[] times, double t)
    {
        int j = LowerBound(times, t);
        int best = -1;
        foreach (int candidate in (int[])[j - 1, j])
        {
            if (candidate < 0 || candidate >= times.Length)
                continue;
            if (best < 0 || Math.Abs(times[candidate] - t) < Math.Abs(times[best] - t))
                best = candidate;
        }

        return best;
    }
}
