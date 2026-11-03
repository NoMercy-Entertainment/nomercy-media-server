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

namespace NoMercy.Encoder.ContentAnalysis.Fingerprinting;

/// <summary>
/// A chromaprint-derived audio fingerprint. Each element of
/// <see cref="Hashes"/> represents one hop of
/// <see cref="ChromaprintHopSamples"/> samples at the
/// <see cref="ChromaprintSampleRate"/> Hz mono rate chromaprint uses
/// internally (~0.1238 s); similar frames have small Hamming distance
/// between their 32-bit hashes.
/// </summary>
/// <param name="Hashes">Chromaprint hash values in frame order.</param>
/// <param name="FrameDuration">
/// Real-time duration one hash represents —
/// <see cref="ChromaprintFrameDuration"/> for real chromaprint output. It is
/// carried so a cheap test fixture can fake a different rate.
/// </param>
/// <param name="StartTime">
/// When the first hash corresponds to in the source file. Needed because
/// we fingerprint a window (first N minutes for intros, last N for outros)
/// rather than the whole file.
/// </param>
public record AudioFingerprint(uint[] Hashes, TimeSpan FrameDuration, TimeSpan StartTime)
{
    /// <summary>The mono sample rate chromaprint analyses audio at.</summary>
    public const int ChromaprintSampleRate = 11025;

    /// <summary>
    /// Samples between two consecutive hashes: chromaprint's 4096-sample frame
    /// advances by a third of itself (4096 / 3 = 1365). Not the 2048 figure that
    /// was used before — that stretched every marker time by about 1.5.
    /// </summary>
    public const int ChromaprintHopSamples = 1365;

    /// <summary>Real-time duration of one chromaprint hash, ~0.12381 s.</summary>
    public static readonly TimeSpan ChromaprintFrameDuration = TimeSpan.FromSeconds(
        (double)ChromaprintHopSamples / ChromaprintSampleRate
    );

    public TimeSpan EndTime => StartTime + FrameDuration * Hashes.Length;

    public TimeSpan FrameToTime(int frameIndex) => StartTime + FrameDuration * frameIndex;
}

/// <summary>
/// Fingerprinting window. Keeping it small matters — a 5-minute window at
/// one hash per <see cref="AudioFingerprint.ChromaprintFrameDuration"/>
/// yields ~2400 frames and every pairwise compare is quadratic.
/// </summary>
public record FingerprintWindow(TimeSpan Start, TimeSpan Duration);
