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

using System.Buffers.Binary;

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>
/// The chromaprint muxer's <c>-fp_format raw</c> output and its timing.
/// <para>
/// The muxer writes the hashes as binary little-endian <c>uint32</c>, four bytes
/// per hash, with no header and no separators (#253: the old detector read this
/// as comma-separated text and got values like "2, 2, 2" from stray bytes).
/// </para>
/// <para>
/// One hash per <see cref="HopSamples"/> samples at <see cref="SampleRate"/> Hz
/// (#345: the old detector used 2048 samples, 1.5x too long). The hop is
/// chromaprint's 4096-sample frame minus its integer overlap
/// (4096 - 4096 / 3 = 2731), so 1365 samples. Measured on the research corpus:
/// a least-squares fit of hash count against duration over 81 whole-track
/// fingerprints gives 1364.998 samples per hash.
/// </para>
/// </summary>
public static class ChromaprintRaw
{
    /// <summary>The mono sample rate chromaprint analyses audio at.</summary>
    public const int SampleRate = 11025;

    /// <summary>Samples between two consecutive hashes.</summary>
    public const int HopSamples = 1365;

    /// <summary>Real time of one hash: 1365 / 11025 = 0.1238095 s.</summary>
    public const double SecondsPerHash = (double)HopSamples / SampleRate;

    /// <summary>
    /// The value the research prototype used (4096 / 3 / 11025 = 0.1238390 s).
    /// Only the fidelity tests use it, to replay the prototype's recorded matches
    /// exactly; it drifts 0.3 s over a 23-minute episode against the real hop.
    /// </summary>
    public const double PrototypeSecondsPerHash = 4096.0 / 3.0 / SampleRate;

    /// <summary>
    /// Reads raw muxer output. A trailing partial hash (fewer than four bytes,
    /// e.g. a truncated file) is dropped.
    /// </summary>
    public static uint[] Parse(ReadOnlySpan<byte> raw)
    {
        int count = raw.Length / sizeof(uint);
        uint[] hashes = new uint[count];
        for (int i = 0; i < count; i++)
            hashes[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(i * sizeof(uint)));
        return hashes;
    }

    /// <summary>Start of hash <paramref name="index"/> in seconds from the start of its part.</summary>
    public static double HashToSeconds(int index, double secondsPerHash = SecondsPerHash) =>
        index * secondsPerHash;
}
