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
/// What stage 1 measured for one episode: everything the pure stages need
/// before any picture probe. <see cref="Black"/> and <see cref="Ocr"/> are null
/// when that pass did not run (OCR is optional: no tesseract model, no OCR).
/// </summary>
/// <param name="Key">A stable name for the episode, unique within its season.</param>
/// <param name="DurationSeconds">Length of the analysed audio track.</param>
/// <param name="Fingerprint">Chromaprint parts; the server writes one, for the whole track.</param>
/// <param name="Silences">silencedetect (-50 dB, 0.3 s) runs, sorted, an open run closed at the end.</param>
/// <param name="Black">blackdetect runs around every silence and over the tail.</param>
/// <param name="Ocr">Text density over the tail.</param>
public sealed record EpisodeSignals(
    string Key,
    double DurationSeconds,
    IReadOnlyList<FingerprintPart> Fingerprint,
    IReadOnlyList<TimeRange> Silences,
    BlackRuns? Black,
    OcrScan? Ocr
);
