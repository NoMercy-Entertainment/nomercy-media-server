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

namespace NoMercy.Providers.Lyrics;

/// <summary>
/// Resolves a track's lyrics across providers (Lrclib preferred, Musixmatch
/// fallback), validating every candidate against the requested track.
/// </summary>
public interface ILyricsAggregator
{
    Task<LyricsFetchResult> SearchLyrics(Track track);

    /// <summary>
    /// Every validated candidate from every provider stage, not only the
    /// winner <see cref="SearchLyrics" /> picks: for a caller that wants to
    /// choose or combine the texts itself.
    /// </summary>
    Task<LyricsCandidatesResult> SearchCandidatesAsync(Track track);
}
