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
/// blackdetect runs of one episode, from two detectors over the same frames:
/// <c>Strict</c> (pix_th 0.10, pic_th 0.98: plain black) and <c>Loose</c>
/// (pix_th 0.10, pic_th 0.80: dark frames with some text, i.e. credits; the
/// default 0.98 misses scrolling credits).
/// </summary>
public sealed record BlackRuns(IReadOnlyList<TimeRange> Strict, IReadOnlyList<TimeRange> Loose);
