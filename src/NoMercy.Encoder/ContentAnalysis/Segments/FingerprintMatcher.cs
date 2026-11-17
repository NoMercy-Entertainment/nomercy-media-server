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

using System.Numerics;

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>
/// Stage 2: the audio two episodes share.
/// <para>
/// Every offset between the two prints is tried. A cheaper exact-hash voting
/// shortcut missed a 39 s shared run in Hellsing, so there is no shortcut here.
/// At one offset, a hash matches when at most <see cref="SegmentRules.MaxDifferingBits"/>
/// of its 32 bits differ; matches up to <see cref="SegmentRules.MaxGapHashes"/>
/// apart form one run, and runs shorter than <see cref="SegmentRules.MinRunSeconds"/>
/// are dropped. Across offsets the longest runs win and no two kept runs overlap
/// in the first print.
/// </para>
/// </summary>
public static class FingerprintMatcher
{
    /// <summary>Shared audio between two episodes, in seconds of each.</summary>
    public static List<AudioMatch> MatchEpisodes(
        IReadOnlyList<FingerprintPart> x,
        IReadOnlyList<FingerprintPart> y,
        double secondsPerHash = ChromaprintRaw.SecondsPerHash
    )
    {
        List<AudioMatch> matches = [];
        foreach (FingerprintPart px in x)
        {
            foreach (FingerprintPart py in y)
            {
                foreach (HashRun run in MatchParts(px.Hashes, py.Hashes, secondsPerHash))
                {
                    matches.Add(
                        new(
                            px.OffsetSeconds + run.AStart * secondsPerHash,
                            px.OffsetSeconds + run.AEnd * secondsPerHash,
                            py.OffsetSeconds + run.BStart * secondsPerHash,
                            py.OffsetSeconds + run.BEnd * secondsPerHash
                        )
                    );
                }
            }
        }

        return matches;
    }

    /// <summary>Shared runs between two prints, longest first, never overlapping in <paramref name="a"/>.</summary>
    internal static List<HashRun> MatchParts(uint[] a, uint[] b, double secondsPerHash)
    {
        List<HashRun> found = [];
        for (int offset = -(b.Length - 1); offset < a.Length; offset++)
            RunsAt(a, b, offset, secondsPerHash, found);

        // Longest first; equal lengths keep their offset order (a stable sort).
        List<(HashRun Run, int Order)> ordered = [.. found.Select((run, order) => (run, order))];
        ordered.Sort(
            (l, r) =>
                l.Run.Length != r.Run.Length
                    ? r.Run.Length.CompareTo(l.Run.Length)
                    : l.Order.CompareTo(r.Order)
        );

        List<HashRun> kept = [];
        foreach ((HashRun run, int _) in ordered)
        {
            bool free = true;
            foreach (HashRun k in kept)
            {
                if (run.AEnd > k.AStart && run.AStart < k.AEnd)
                {
                    free = false;
                    break;
                }
            }

            if (free)
                kept.Add(run);
        }

        return kept;
    }

    /// <summary>
    /// The runs at one offset (index in <paramref name="a"/> minus index in
    /// <paramref name="b"/>), appended to <paramref name="found"/>. An offset whose
    /// matching hashes add up to less than the minimum run contributes nothing,
    /// even when bridged gaps make one of its runs long enough.
    /// </summary>
    private static void RunsAt(
        uint[] a,
        uint[] b,
        int offset,
        double secondsPerHash,
        List<HashRun> found
    )
    {
        int a0 = Math.Max(offset, 0);
        int b0 = Math.Max(-offset, 0);
        int n = Math.Min(a.Length - a0, b.Length - b0);
        if (n * secondsPerHash < SegmentRules.MinRunSeconds)
            return;

        ReadOnlySpan<uint> sa = a.AsSpan(a0, n);
        ReadOnlySpan<uint> sb = b.AsSpan(b0, n);
        int firstNew = found.Count;
        int matching = 0;
        int start = -1;
        int previous = -1;

        for (int i = 0; i < n; i++)
        {
            if (BitOperations.PopCount(sa[i] ^ sb[i]) > SegmentRules.MaxDifferingBits)
                continue;

            matching++;
            if (start < 0)
            {
                start = i;
            }
            else if (i - previous > SegmentRules.MaxGapHashes)
            {
                AddRun(found, a0, b0, start, previous + 1, secondsPerHash);
                start = i;
            }

            previous = i;
        }

        if (start >= 0)
            AddRun(found, a0, b0, start, previous + 1, secondsPerHash);

        if (matching * secondsPerHash < SegmentRules.MinRunSeconds)
            found.RemoveRange(firstNew, found.Count - firstNew);
    }

    private static void AddRun(
        List<HashRun> found,
        int a0,
        int b0,
        int start,
        int end,
        double secondsPerHash
    )
    {
        if ((end - start) * secondsPerHash >= SegmentRules.MinRunSeconds)
            found.Add(new(a0 + start, a0 + end, b0 + start, b0 + end));
    }
}
