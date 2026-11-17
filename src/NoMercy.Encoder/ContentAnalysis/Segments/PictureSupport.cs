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
/// Stage 5 output: of <paramref name="Checked"/> partners whose picture could be
/// aligned with this candidate, <paramref name="Matched"/> showed the same picture.
/// </summary>
public readonly record struct PictureSupport(int Matched, int Checked)
{
    /// <summary>
    /// Absolute, not a majority: partners whose opening song plays under the
    /// credits (Rick and Morty) share the sound but not the picture. Reused score
    /// matched in at most 1 partner (Squid Game); real themes in 2 or more.
    /// </summary>
    public bool Confirmed => Matched >= Math.Min(2, Checked);
}
