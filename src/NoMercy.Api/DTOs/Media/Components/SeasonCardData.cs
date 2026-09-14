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
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Data for NMSeasonCard component - displays an episode in a season.
/// </summary>
public record SeasonCardData
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

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    public SeasonCardData() { }

    public SeasonCardData(Episode episode)
    {
        string? title = episode.Translations.FirstOrDefault()?.Title;
        string? overview = episode.Translations.FirstOrDefault()?.Overview;

        TvId = episode.TvId;
        Id = episode.Id;
        Title = title.OrWhenEmpty(episode.Title);
        Overview = overview.OrWhenEmpty(episode.Overview);
        EpisodeNumber = episode.EpisodeNumber;
        SeasonNumber = episode.SeasonNumber;
        AirDate = episode.AirDate;
        Still = episode.Still;
        ColorPalette = episode.ColorPalette;
        Available = episode.VideoFiles.Count != 0;
        Link = new(
            $"/tv/{TvId}/watch?season={SeasonNumber}&episode={EpisodeNumber}",
            UriKind.Relative
        );
    }

    public SeasonCardData(EpisodeDto dto)
    {
        Id = dto.Id;
        EpisodeNumber = dto.EpisodeNumber;
        SeasonNumber = dto.SeasonNumber;
        Title = dto.Title;
        Overview = dto.Overview;
        AirDate = dto.AirDate;
        Still = dto.Still;
        ColorPalette = dto.ColorPalette;
        Progress = dto.Progress;
        Available = dto.Available;
        TvId = dto.TvId;
        Link = dto.Link;
    }

    public SeasonCardData(MissingEpisodeDto dto)
    {
        Id = dto.Id;
        EpisodeNumber = dto.EpisodeNumber;
        SeasonNumber = dto.SeasonNumber;
        Title = dto.Title;
        Overview = dto.Overview;
        AirDate = dto.AirDate;
        Still = dto.Still;
        ColorPalette = dto.ColorPalette;
        Progress = dto.Progress;
        Available = dto.Available;
        TvId = dto.TvId;
        Link = dto.Link;
    }
}
