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

using NoMercy.Database.Models.Music;

namespace NoMercy.Data.Services.Music;

/// <summary>
/// Lyrics as stored for a track, with their provenance. <paramref name="Lines" />
/// carry the stored times; the caller applies the track's offset. Source and
/// timing are null on rows written before provenance was recorded.
/// </summary>
/// <param name="Lines">The stored lines, never empty.</param>
/// <param name="Source">Where the text came from: a <see cref="LyricsSources" /> value.</param>
/// <param name="Timing">Where the timing came from: a <see cref="LyricsTimings" /> value.</param>
public sealed record ResolvedLyrics(Lyric[] Lines, string? Source, string? Timing);
