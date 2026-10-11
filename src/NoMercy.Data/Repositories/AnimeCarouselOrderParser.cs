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

namespace NoMercy.Data.Repositories;

public static class AnimeCarouselOrderParser
{
    /// <summary>
    /// Reads the mode name a client sends. A missing or unknown name gives the
    /// default of the group kind, so an old or new client never gets an error.
    /// </summary>
    public static AnimeCarouselOrder Parse(string? value, AnimeGroupKind kind)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "random_daily" => AnimeCarouselOrder.RandomDaily,
            "newest" => AnimeCarouselOrder.Newest,
            "most_titles" => AnimeCarouselOrder.MostTitles,
            "most_watched" => AnimeCarouselOrder.MostWatched,
            _ => kind == AnimeGroupKind.Season
                ? AnimeCarouselOrder.Newest
                : AnimeCarouselOrder.RandomDaily,
        };
    }
}
