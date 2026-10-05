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

using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NoMercy.Database.Models.Common;

[PrimaryKey(nameof(Id))]
[Index(nameof(Year))]
[Index(nameof(Quarter))]
public class AnimeSeason
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("year")]
    public int Year { get; set; }

    // WINTER, SPRING, SUMMER, or FALL — matches AniList's season enum,
    // since AniList is this feature's primary source for season data.
    [JsonProperty("quarter")]
    public string Quarter { get; set; } = string.Empty;

    public ICollection<AnimeSeasonMovie> AnimeSeasonMovies { get; set; } = [];
    public ICollection<AnimeSeasonTv> AnimeSeasonTvShows { get; set; } = [];
}
