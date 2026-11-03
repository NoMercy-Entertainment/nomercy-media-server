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

namespace NoMercy.MediaProcessing.Shows;

public interface IAnimeEnrichmentService
{
    /// <summary>
    /// Classifies a show and, when it is anime, stores its themes, season and
    /// demographics. Never throws: a provider failure must not abort an import.
    /// </summary>
    /// <returns>
    /// True when the show is settled: enriched, already enriched, or answered
    /// "not anime". False when AniList and Jikan could not answer (an outage,
    /// a timeout, a rate limit), so a caller that walks titles can retry it later.
    /// </returns>
    Task<bool> EnrichTvAsync(
        int tvId,
        string title,
        int? year,
        string[]? originCountry,
        bool? priority = false
    );

    /// <summary>Movie counterpart of <see cref="EnrichTvAsync"/>, with the same result.</summary>
    Task<bool> EnrichMovieAsync(
        int movieId,
        string title,
        int? year,
        string[]? originCountry,
        bool? priority = false
    );
}
