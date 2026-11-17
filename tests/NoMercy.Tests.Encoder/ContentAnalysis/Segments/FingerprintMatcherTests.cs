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

using NoMercy.Encoder.ContentAnalysis.Segments;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments;

/// <summary>Stage 2: exhaustive pairwise matching.</summary>
[Trait("Category", "Unit")]
public class FingerprintMatcherTests
{
    private const double Hop = ChromaprintRaw.SecondsPerHash;

    [Fact]
    public void SharedAudio_IsFoundAtItsAbsolutePlaceInBothEpisodes()
    {
        // 400 hashes (49.5 s) shared: at 61.9 s in the first episode and at
        // 148.6 s in the second. Times are absolute and positive (#344) and run
        // at 0.1238 s per hash (#345).
        uint[] a = Noise(seed: 1, count: 3000);
        uint[] b = Noise(seed: 2, count: 3000);
        Array.Copy(a, 500, b, 1200, 400);
        FlipBits(b, 1200, 400, bits: 10);
        Isolate(a, 500, b, 1200, 400);

        List<AudioMatch> matches = FingerprintMatcher.MatchEpisodes([new(0, a)], [new(0, b)]);

        matches.Should().ContainSingle();
        AudioMatch match = matches[0];
        match.XStart.Should().BeApproximately(500 * Hop, 1e-9);
        match.XEnd.Should().BeApproximately(900 * Hop, 1e-9);
        match.YStart.Should().BeApproximately(1200 * Hop, 1e-9);
        match.YEnd.Should().BeApproximately(1600 * Hop, 1e-9);
    }

    [Fact]
    public void Hashes_WithMoreThanTenDifferingBits_DoNotMatch()
    {
        uint[] a = Noise(seed: 3, count: 1000);
        uint[] b = Noise(seed: 4, count: 1000);
        Array.Copy(a, 100, b, 100, 300);
        FlipBits(b, 100, 300, bits: 11);

        FingerprintMatcher.MatchEpisodes([new(0, a)], [new(0, b)]).Should().BeEmpty();
    }

    [Fact]
    public void MatchesUpToEightHashesApart_StayOneRun_AndFurtherApartSplitIt()
    {
        uint[] a = Noise(seed: 5, count: 2000);
        uint[] bridged = Noise(seed: 6, count: 2000);
        Array.Copy(a, 0, bridged, 0, 2000);
        // 7 differing hashes in the middle of 300 shared ones (matches 8 apart):
        // one run. One more (9 apart): two runs.
        for (int i = 600; i < 607; i++)
            bridged[i] = ~a[i];
        uint[] split = (uint[])bridged.Clone();
        split[607] = ~a[607];

        List<AudioMatch> one = FingerprintMatcher.MatchEpisodes(
            [new(0, a[450..750])],
            [new(0, bridged[450..750])]
        );
        List<AudioMatch> two = FingerprintMatcher.MatchEpisodes(
            [new(0, a[450..750])],
            [new(0, split[450..750])]
        );

        one.Should().ContainSingle().Which.XEnd.Should().BeApproximately(300 * Hop, 1e-9);
        two.Should().HaveCount(2);
    }

    [Fact]
    public void Runs_ShorterThanEightSeconds_AreDropped()
    {
        uint[] a = Noise(seed: 7, count: 1000);
        uint[] b = Noise(seed: 8, count: 1000);
        Array.Copy(a, 100, b, 300, 64); // 7.92 s

        FingerprintMatcher.MatchEpisodes([new(0, a)], [new(0, b)]).Should().BeEmpty();

        Array.Copy(a, 100, b, 300, 65); // 8.05 s
        FingerprintMatcher.MatchEpisodes([new(0, a)], [new(0, b)]).Should().ContainSingle();
    }

    [Fact]
    public void PartOffsets_AreAddedToTheTimes()
    {
        uint[] a = Noise(seed: 9, count: 500);
        uint[] b = Noise(seed: 10, count: 500);
        Array.Copy(a, 0, b, 0, 200);

        AudioMatch match = FingerprintMatcher
            .MatchEpisodes([new(1000, a)], [new(2000, b)])
            .Should()
            .ContainSingle()
            .Subject;

        match.XStart.Should().BeApproximately(1000, 1e-9);
        match.YStart.Should().BeApproximately(2000, 1e-9);
    }

    private static uint[] Noise(int seed, int count)
    {
        Random random = new(seed);
        uint[] hashes = new uint[count];
        for (int i = 0; i < count; i++)
            hashes[i] = (uint)random.NextInt64(0, 1L << 32);
        return hashes;
    }

    /// <summary>
    /// Makes the hashes around a shared stretch differ in every bit, so a chance
    /// match next to it (2.5 % of random pairs) cannot extend the run.
    /// </summary>
    private static void Isolate(uint[] a, int aStart, uint[] b, int bStart, int count)
    {
        for (int k = 1; k <= SegmentRules.MaxGapHashes; k++)
        {
            if (aStart - k >= 0 && bStart - k >= 0)
                b[bStart - k] = ~a[aStart - k];
            if (aStart + count - 1 + k < a.Length && bStart + count - 1 + k < b.Length)
                b[bStart + count - 1 + k] = ~a[aStart + count - 1 + k];
        }
    }

    private static void FlipBits(uint[] hashes, int start, int count, int bits)
    {
        Random random = new(start + bits);
        for (int i = start; i < start + count; i++)
        {
            uint mask = 0;
            while (System.Numerics.BitOperations.PopCount(mask) < bits)
                mask |= 1u << random.Next(32);
            hashes[i] ^= mask;
        }
    }
}
