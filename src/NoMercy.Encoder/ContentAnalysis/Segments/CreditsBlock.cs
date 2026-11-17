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
/// Stage 7 output: a stretch the picture shows as credits, and what showed it.
/// </summary>
/// <param name="FromDarkFrames">Dark frames (loose blackdetect) found part of it.</param>
/// <param name="FromText">OCR text frames found part of it.</param>
public readonly record struct CreditsBlock(
    double Start,
    double End,
    bool FromDarkFrames,
    bool FromText
)
{
    public double Length => End - Start;
}
