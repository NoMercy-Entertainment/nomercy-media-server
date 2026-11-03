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

namespace NoMercy.Encoder.ContentAnalysis.Fingerprinting;

/// <summary>
/// Default detector — pairwise sliding-window comparison between
/// fingerprints. Two frames are considered a match when the Hamming
/// distance between their 32-bit hashes is ≤ <see cref="_bitDistanceThreshold"/>,
/// borrowed from the AcoustID literature where anything under ~8 bits
/// (out of 32) is visually similar audio.
///
/// For the intro we look at the leading frames; for outros we flip the
/// analysis so the tail aligns at frame N. The returned marker spans the
/// longest run of consecutive matches present in all fingerprints.
/// </summary>
public class ChromaprintIntroDetector : IIntroDetector
{
    private readonly int _bitDistanceThreshold;
    private readonly int _minMatchFrames;

    public ChromaprintIntroDetector(int bitDistanceThreshold = 8, int minMatchFrames = 25)
    {
        _bitDistanceThreshold = bitDistanceThreshold;
        // ~3 seconds at chromaprint's frame rate (25 x 0.1238 s) — below this,
        // matches are too short to be a real intro and are likely noise.
        _minMatchFrames = minMatchFrames;
    }

    public IntroMarker? DetectIntro(IReadOnlyList<AudioFingerprint> episodeFingerprints) =>
        Detect(episodeFingerprints, fromTail: false);

    public IntroMarker? DetectOutro(IReadOnlyList<AudioFingerprint> episodeFingerprints) =>
        Detect(episodeFingerprints, fromTail: true);

    private IntroMarker? Detect(IReadOnlyList<AudioFingerprint> prints, bool fromTail)
    {
        if (prints.Count < 2)
            return null;

        AudioFingerprint reference = prints[0];
        if (reference.Hashes.Length == 0)
            return null;

        // Find the best-aligned match region between reference and each
        // other print. The intro region is the intersection of all
        // pairwise matches — frames that match for every other episode.
        bool[] matchMask = InitialMask(reference.Hashes.Length);

        for (int i = 1; i < prints.Count; i++)
        {
            AudioFingerprint other = prints[i];
            if (other.Hashes.Length == 0)
                return null;

            (int refOffset, int otherOffset, int length) = BestAlignment(
                reference.Hashes,
                other.Hashes
            );

            if (length < _minMatchFrames)
                return null;

            // Carry over only the frames that participated in THIS pair's
            // match. Anything outside [refOffset, refOffset + length) is
            // not shared with this episode and must be excluded.
            for (int f = 0; f < matchMask.Length; f++)
            {
                if (f < refOffset || f >= refOffset + length)
                    matchMask[f] = false;
            }

            // Within the matched slice, mark frames whose actual Hamming
            // distance is above threshold as non-matches (the alignment
            // search picked the BEST offset; a few frames inside might
            // still diverge).
            for (int k = 0; k < length; k++)
            {
                int refIdx = refOffset + k;
                int otherIdx = otherOffset + k;
                if (!matchMask[refIdx])
                    continue;
                int dist = BitOperations.PopCount(
                    reference.Hashes[refIdx] ^ other.Hashes[otherIdx]
                );
                if (dist > _bitDistanceThreshold)
                    matchMask[refIdx] = false;
            }
        }

        (int runStart, int runLength) = LongestTrueRun(matchMask);
        if (runLength < _minMatchFrames)
            return null;

        TimeSpan start = reference.FrameToTime(runStart);
        TimeSpan end = reference.FrameToTime(runStart + runLength);

        // For outro detection we ran the same comparison but the caller
        // fingerprinted the tails; the resulting marker is already in
        // real-time source coordinates thanks to FingerprintWindow.Start.
        double confidence = (double)runLength / Math.Max(1, reference.Hashes.Length);

        // Suppress the "fromTail" argument — kept for API symmetry and to
        // let future refinements weight tail matches differently.
        _ = fromTail;

        return new(start, end, confidence);
    }

    /// <summary>
    /// Returns the alignment of <paramref name="b"/> against
    /// <paramref name="a"/> that produces the longest contiguous run of
    /// low-Hamming-distance frames. Quadratic in the fingerprint size;
    /// fine for the ~2400-frame windows we work with (&lt;5 min each).
    /// </summary>
    internal (int aStart, int bStart, int length) BestAlignment(uint[] a, uint[] b)
    {
        int bestLen = 0;
        int bestA = 0;
        int bestB = 0;

        // Scan every possible alignment offset. offset > 0 means b is
        // shifted forward relative to a; offset < 0 means a is shifted
        // forward. Either direction is legal — an intro can start at a
        // slightly different absolute offset in each episode.
        int minOffset = -b.Length + 1;
        int maxOffset = a.Length - 1;

        for (int offset = minOffset; offset <= maxOffset; offset++)
        {
            int aIdx = Math.Max(0, offset);
            int bIdx = Math.Max(0, -offset);

            int overlap = Math.Min(a.Length - aIdx, b.Length - bIdx);
            if (overlap <= bestLen)
                continue;

            int currentRun = 0;
            int runStartA = aIdx;
            int runStartB = bIdx;

            for (int k = 0; k < overlap; k++)
            {
                int ai = aIdx + k;
                int bi = bIdx + k;
                int dist = BitOperations.PopCount(a[ai] ^ b[bi]);

                if (dist <= _bitDistanceThreshold)
                {
                    if (currentRun == 0)
                    {
                        runStartA = ai;
                        runStartB = bi;
                    }
                    currentRun++;
                    if (currentRun > bestLen)
                    {
                        bestLen = currentRun;
                        bestA = runStartA;
                        bestB = runStartB;
                    }
                }
                else
                {
                    currentRun = 0;
                }
            }
        }

        return (bestA, bestB, bestLen);
    }

    private static bool[] InitialMask(int length)
    {
        bool[] mask = new bool[length];
        Array.Fill(mask, true);
        return mask;
    }

    private static (int start, int length) LongestTrueRun(bool[] mask)
    {
        int bestStart = 0;
        int bestLen = 0;
        int currentStart = 0;
        int currentLen = 0;

        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i])
            {
                if (currentLen == 0)
                    currentStart = i;
                currentLen++;
                if (currentLen > bestLen)
                {
                    bestLen = currentLen;
                    bestStart = currentStart;
                }
            }
            else
            {
                currentLen = 0;
            }
        }

        return (bestStart, bestLen);
    }
}
