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
/// On-screen text density from FFmpeg's <c>ocr</c> filter at 1 frame per second,
/// from <paramref name="StartSeconds"/> to the end of the episode.
/// </summary>
public sealed record OcrScan(double StartSeconds, IReadOnlyList<OcrSample> Samples);
