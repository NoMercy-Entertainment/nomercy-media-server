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

/// <summary>Keys of server-wide settings stored in the Configuration table.</summary>
public static class ServerConfigurationKeys
{
    public const string ServerName = "serverName";

    // The order of the anime carousels on the library overview: random_daily,
    // newest, most_titles or most_watched. Unset uses the default of each kind.
    public const string AnimeCarouselOrderThemes = "anime_carousel_order_themes";
    public const string AnimeCarouselOrderDemographics = "anime_carousel_order_demographics";
    public const string AnimeCarouselOrderSeasons = "anime_carousel_order_seasons";
}
