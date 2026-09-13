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

namespace NoMercy.Api.DTOs.Media;

public record SeasonDto
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("season_number")]
    public long SeasonNumber { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("episodes")]
    public IEnumerable<EpisodeDto> Episodes { get; set; }

    [JsonProperty("translations")]
    public IEnumerable<TranslationDto> Translations { get; set; }

    public SeasonDto(Season season)
    {
        string? title = season.Translations.FirstOrDefault()?.Title;
        string? overview = season.Translations.FirstOrDefault()?.Overview;

        Id = season.Id;
        Title = !string.IsNullOrEmpty(title) ? title : season.Title;
        Overview = !string.IsNullOrEmpty(overview) ? overview : season.Overview;
        Poster = season.Poster;
        SeasonNumber = season.SeasonNumber;
        ColorPalette = season.ColorPalette;
        Translations = season.Translations.Select(translation => new TranslationDto(translation));
        Episodes = season
            .Episodes.OrderBy(episode => episode.EpisodeNumber)
            .Select(episode => new EpisodeDto(episode));
    }
}
