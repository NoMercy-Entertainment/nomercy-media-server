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

using NoMercy.Providers.NoMercy.Models;

namespace NoMercy.Providers.Lyrics;

/// <summary>
/// One validated provider candidate with where it came from: a
/// <see cref="NoMercy.Database.Models.Music.LyricsSources" /> value.
/// </summary>
public sealed record LyricsTextCandidate(string Source, LyricCandidate Candidate);
