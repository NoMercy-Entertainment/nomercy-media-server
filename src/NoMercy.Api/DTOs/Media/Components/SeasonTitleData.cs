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

using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Data for NMSeasonTitle component - displays a season header.
/// </summary>
public record SeasonTitleData
{
    [JsonProperty("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("episodeCount")]
    public int EpisodeCount { get; set; }

    public SeasonTitleData() { }

    public SeasonTitleData(int seasonNumber, int episodeCount)
    {
        SeasonNumber = seasonNumber;
        Title = $"Season {seasonNumber}";
        EpisodeCount = episodeCount;
    }
}
