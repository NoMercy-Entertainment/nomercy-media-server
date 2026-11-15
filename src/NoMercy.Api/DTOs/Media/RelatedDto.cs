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
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.Api.DTOs.Media;

public record RelatedDto
{
    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("adult")]
    public bool? Adult { get; set; }

    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("titleSort")]
    public string? TitleSort { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("media_type")]
    public string MediaType { get; set; }

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    public RelatedDto(Recommendation recommendation, string type, Tv[]? recommendations = null)
    {
        Id = recommendation.MediaId;
        Adult = recommendation.Adult;
        Overview = recommendation.Overview;
        Poster = recommendation.Poster;
        Backdrop = recommendation.Backdrop;
        Title = recommendation.Title;
        TitleSort = recommendation.TitleSort;
        Type = type;
        MediaType = type;
        ColorPalette = recommendation.ColorPalette;
        Link = new($"/{type}/{recommendation.MediaId}", UriKind.Relative);
        NumberOfItems =
            type == "tv"
                ? recommendations
                    ?.FirstOrDefault(t => t.Id == recommendation.MediaId)
                    ?.NumberOfEpisodes
                : null;
        HaveItems =
            type == "tv"
                ? recommendations
                    ?.FirstOrDefault(t => t.Id == recommendation.MediaId)
                    ?.Episodes.Where(e => e.SeasonNumber > 0)
                    .Count(episode => episode.VideoFiles.Any(videoFile => videoFile.Folder != null))
                : null;
    }

    public RelatedDto(Similar similar, string type, Tv[]? similars = null)
    {
        Id = similar.MediaId;
        Overview = similar.Overview;
        Poster = similar.Poster;
        Backdrop = similar.Backdrop;
        Title = similar.Title;
        TitleSort = similar.TitleSort;
        Type = type;
        MediaType = type;
        ColorPalette = similar.ColorPalette;
        Link = new($"/{type}/{similar.MediaId}", UriKind.Relative);
        NumberOfItems =
            type == "tv"
                ? similars?.FirstOrDefault(s => s.Id == similar.MediaId)?.NumberOfEpisodes
                : null;
        HaveItems =
            type == "tv"
                ? similars
                    ?.FirstOrDefault(t => t.Id == similar.MediaId)
                    ?.Episodes.Where(e => e.SeasonNumber > 0)
                    .Count(episode => episode.VideoFiles.Any(videoFile => videoFile.Folder != null))
                : null;
    }

    public RelatedDto(TmdbMovie tmdbSimilar, string type)
    {
        Id = tmdbSimilar.Id;
        Adult = tmdbSimilar.Adult;
        Overview = tmdbSimilar.Overview;
        Poster = tmdbSimilar.PosterPath;
        Backdrop = tmdbSimilar.BackdropPath;
        Title = tmdbSimilar.Title;
        TitleSort = tmdbSimilar.Title.TitleSort(tmdbSimilar.ReleaseDate);
        Type = type;
        MediaType = type;
        Link = new($"/{type}/{tmdbSimilar.Id}", UriKind.Relative);
        ColorPalette = new();
        NumberOfItems = 0;
        HaveItems = 0;
    }

    public RelatedDto(TmdbTvShow recommendation, string type)
    {
        Id = recommendation.Id;
        Overview = recommendation.Overview;
        Poster = recommendation.PosterPath;
        Backdrop = recommendation.BackdropPath;
        Title = recommendation.Name;
        TitleSort = recommendation.Name.TitleSort();
        Type = type;
        MediaType = type;
        Link = new($"/{type}/{recommendation.Id}", UriKind.Relative);
        ColorPalette = new();
        NumberOfItems = 0;
        HaveItems = 0;
    }
}
