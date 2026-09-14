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
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public class EpisodeDto
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("episode_number")]
    public long EpisodeNumber { get; set; }

    [JsonProperty("season_number")]
    public long SeasonNumber { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("airDate")]
    public DateTime? AirDate { get; set; }

    [JsonProperty("still")]
    public string? Still { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("progress")]
    public object? Progress { get; set; }

    [JsonProperty("available")]
    public bool Available { get; set; }

    [JsonProperty("tv_id")]
    public int TvId { get; set; }

    [JsonProperty("translations")]
    public IEnumerable<TranslationDto> Translations { get; set; } = [];

    [JsonProperty("link")]
    public Uri Link =>
        new($"/tv/{TvId}/watch?season={SeasonNumber}&episode={EpisodeNumber}", UriKind.Relative);

    public EpisodeDto(Episode episode)
    {
        string? title = episode.Translations.FirstOrDefault()?.Title;
        string? overview = episode.Translations.FirstOrDefault()?.Overview;

        VideoFile? videoFile = episode.VideoFiles.FirstOrDefault();
        UserData? userData = videoFile?.UserData.FirstOrDefault();

        TvId = episode.TvId;
        Id = episode.Id;
        Title = !string.IsNullOrEmpty(title) ? title : episode.Title;
        Overview = !string.IsNullOrEmpty(overview) ? overview : episode.Overview;
        EpisodeNumber = episode.EpisodeNumber;
        SeasonNumber = episode.SeasonNumber;
        AirDate = episode.AirDate;
        Still = episode.Still;
        ColorPalette = episode.ColorPalette;
        Available = episode.VideoFiles.Count != 0;
        Translations = episode.Translations.Select(translation => new TranslationDto(translation));

        Progress =
            userData?.UpdatedAt is not null && videoFile?.Duration is not null
                ? (int)
                    Math.Round(
                        (double)(100 * (userData.Time ?? 0))
                            / (videoFile.Duration?.ToSeconds() ?? 0)
                    )
                : null;
    }
}
