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
/// Stage 3: shared audio becomes theme candidates by position.
/// <para>
/// Shows reuse their score heavily (Hellsing: one cue in all 13 episodes, each
/// time at a different place), so sharing a sound proves nothing. A theme is the
/// same sound in the same zone of most episodes. Per 0.5 s this counts the
/// partners that hold the audio in the same zone (head = first 40 %, tail = last
/// 40 %); a stretch where that count reaches max(2, 40 % of partners) is a
/// candidate. In the second pass a partner also counts when the audio falls
/// inside one of its own first-pass themes or credits: Rick and Morty's opening
/// song also plays under its end credits.
/// </para>
/// </summary>
public static class ThemeFinder
{
    /// <summary>
    /// Theme candidates of episode <paramref name="episode"/>.
    /// </summary>
    /// <param name="durations">Every episode's duration, by season index.</param>
    /// <param name="partners">How many partners this episode was matched against.</param>
    /// <param name="hits">This episode's shared audio with every partner.</param>
    /// <param name="partnerThemes">Pass 2: each partner's pass-1 themes and credits; null in pass 1.</param>
    public static List<ThemeRegion> Find(
        int episode,
        IReadOnlyList<double> durations,
        int partners,
        IReadOnlyList<PartnerHit> hits,
        IReadOnlyList<IReadOnlyList<TimeRange>>? partnerThemes = null
    )
    {
        double duration = durations[episode];
        int bins = (int)Math.Ceiling(duration / SegmentRules.BinSeconds) + 1;
        int episodes = durations.Count;

        // same: same zone here and there, or (pass 2) inside one of the partner's themes.
        // strict: same zone only — the pass-1 evidence edges are trimmed on.
        bool[] same = new bool[bins * episodes];
        bool[] strict = new bool[bins * episodes];
        bool[] any = new bool[bins * episodes];

        foreach (PartnerHit hit in hits)
        {
            Zone zone = ZoneOf(hit.Start, hit.End, duration);
            Zone partnerZone = ZoneOf(hit.PartnerStart, hit.PartnerEnd, durations[hit.Partner]);
            bool inPartnerTheme =
                partnerThemes is not null
                && partnerThemes[hit.Partner]
                    .Any(t =>
                        SegmentMath.Overlap(hit.PartnerStart, hit.PartnerEnd, t.Start, t.End)
                        >= SegmentRules.PartnerThemeOverlap * (hit.PartnerEnd - hit.PartnerStart)
                    );
            bool sameZone = zone != Zone.Middle && zone == partnerZone;
            bool counts = zone != Zone.Middle && (zone == partnerZone || inPartnerTheme);

            int first = Math.Max((int)(hit.Start / SegmentRules.BinSeconds), 0);
            int last = Math.Min((int)Math.Ceiling(hit.End / SegmentRules.BinSeconds), bins);
            for (int k = first; k < last; k++)
            {
                int cell = k * episodes + hit.Partner;
                any[cell] = true;
                if (counts)
                    same[cell] = true;
                if (sameZone)
                    strict[cell] = true;
            }
        }

        int[] sameCount = CountPerBin(same, bins, episodes);
        int[] strictCount = CountPerBin(strict, bins, episodes);
        int need = SegmentMath.NeededPartners(partners);

        List<ThemeRegion> regions = [];
        foreach ((int stretchStart, int stretchEnd) in Stretches(sameCount, need))
        {
            int a = stretchStart;
            int b = stretchEnd;

            // Trim weakly supported edges, on same-zone partners only: music just
            // before Silo E02's opening was shared with 2 partners (one under its
            // credits) while the opening itself had 5.
            int peak = 0;
            for (int k = a; k < b; k++)
                peak = Math.Max(peak, strictCount[k]);
            int core = Math.Max(1, (int)Math.Ceiling(SegmentRules.CoreFraction * peak));
            while (a < b && strictCount[a] < core)
                a++;
            while (b > a && strictCount[b - 1] < core)
                b--;
            if (b - a < 2)
                continue;

            Zone zone = ZoneOf(a * SegmentRules.BinSeconds, b * SegmentRules.BinSeconds, duration);
            if (zone == Zone.Middle)
                continue;

            int consistent = 0;
            for (int k = a; k < b; k++)
                consistent = Math.Max(consistent, sameCount[k]);

            int support = 0;
            List<double> ratios = [];
            for (int p = 0; p < episodes; p++)
            {
                int sameBins = 0;
                bool anyBin = false;
                for (int k = a; k < b; k++)
                {
                    if (same[k * episodes + p])
                        sameBins++;
                    if (any[k * episodes + p])
                        anyBin = true;
                }

                if (anyBin)
                    support++;
                if (sameBins > 0)
                    ratios.Add((double)sameBins / (b - a));
            }

            ratios.Sort();
            double start = a * SegmentRules.BinSeconds;
            regions.Add(
                new(
                    zone == Zone.Head ? ThemeKind.Intro : ThemeKind.Outro,
                    start,
                    b * SegmentRules.BinSeconds,
                    zone == Zone.Head ? start / duration : 0.0,
                    consistent,
                    support,
                    ratios.Count > 0 ? SegmentMath.Round(ratios[ratios.Count / 2], 3) : 0.0
                )
            );
        }

        return regions;
    }

    internal static Zone ZoneOf(double start, double end, double duration)
    {
        double middle = (start + end) / 2;
        if (middle < SegmentRules.HeadZone * duration)
            return Zone.Head;
        return middle > SegmentRules.TailZone * duration ? Zone.Tail : Zone.Middle;
    }

    private static int[] CountPerBin(bool[] cells, int bins, int episodes)
    {
        int[] counts = new int[bins];
        for (int k = 0; k < bins; k++)
        {
            int count = 0;
            for (int p = 0; p < episodes; p++)
            {
                if (cells[k * episodes + p])
                    count++;
            }

            counts[k] = count;
        }

        return counts;
    }

    /// <summary>Runs of bins reaching <paramref name="need"/>, bridging short gaps.</summary>
    private static List<(int Start, int End)> Stretches(int[] counts, int need)
    {
        List<(int Start, int End)> stretches = [];
        int start = -1;
        int last = -1;
        for (int k = 0; k < counts.Length; k++)
        {
            if (counts[k] < need)
                continue;

            if (start < 0)
            {
                start = k;
            }
            else if (k - last > SegmentRules.StretchGapBins)
            {
                stretches.Add((start, last + 1));
                start = k;
            }

            last = k;
        }

        if (start >= 0)
            stretches.Add((start, last + 1));

        return stretches;
    }
}
