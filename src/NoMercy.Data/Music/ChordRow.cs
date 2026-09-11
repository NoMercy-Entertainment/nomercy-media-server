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

namespace NoMercy.Data.Music;

/// <summary>
/// The shape one entry of <c>TrackDjAnalysis.Chords</c> takes on disk:
/// <c>{ ms, chord }</c>. What <see cref="DjAnalysisJson" /> parses that
/// column into — the one shape the plugin reader and the API's DJ analysis
/// DTO both build their own chord type from.
/// </summary>
public sealed record ChordRow(int Ms, string? Chord);
