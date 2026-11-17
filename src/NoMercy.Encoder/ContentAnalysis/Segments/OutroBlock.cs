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
/// The end-of-episode block of stage 8: credits from the picture and/or an
/// ending theme from the audio, rounded to 0.01 s.
/// </summary>
/// <param name="FromDarkFrames">Dark frames found part of the credits.</param>
/// <param name="FromText">OCR text frames found part of the credits.</param>
public sealed record OutroBlock(
    double Start,
    double End,
    double Length,
    OutroSources Sources,
    bool FromDarkFrames,
    bool FromText
)
{
    /// <summary>
    /// Credits found only by OCR, with no dark frames and no ending theme, are
    /// names over the last scene (Krapopolis): a "next episode" button, never an
    /// automatic skip.
    /// </summary>
    public bool ButtonOnly =>
        Sources.HasFlag(OutroSources.Credits)
        && !Sources.HasFlag(OutroSources.Audio)
        && FromText
        && !FromDarkFrames;
}
