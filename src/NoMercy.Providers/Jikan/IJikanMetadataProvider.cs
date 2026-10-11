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

using NoMercy.Providers.Jikan.Models;

namespace NoMercy.Providers.Jikan;

public interface IJikanMetadataProvider
{
    Task<JikanAnime?> SearchAsync(string title, int? year, bool? priority = false);

    // Bypasses the unreliable /anime search endpoint (see JikanClient.GetByIdAsync)
    // for callers that already have a MAL id, e.g. from AniList's idMal.
    Task<JikanAnime?> GetByIdAsync(int malId, bool? priority = false);

    // filter is "themes" or "demographics".
    Task<JikanGenre[]?> GetGenresAsync(string filter, bool? priority = false);
}
