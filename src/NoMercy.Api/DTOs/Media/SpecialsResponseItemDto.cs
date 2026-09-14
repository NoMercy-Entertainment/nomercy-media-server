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
using NoMercy.Api.DTOs.Common;
using NoMercy.Database;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public record SpecialsResponseItemDto
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("watched")]
    public bool Watched { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("titleSort")]
    public string TitleSort { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("genres")]
    public GenreDto[]? Genres { get; set; } = [];

    [JsonProperty("videoId")]
    public string? VideoId { get; set; }

    [JsonProperty("videos")]
    public VideoDto[]? Videos { get; set; } = [];

    [JsonProperty("total_duration")]
    public int TotalDuration { get; set; }

    public SpecialsResponseItemDto(SpecialItem item)
    {
        if (item.Movie is null)
            return;

        string? title = item.Movie.Translations.FirstOrDefault()?.Title;
        string? overview = item.Movie.Translations.FirstOrDefault()?.Overview;

        Id = item.Movie.Id.ToString();
        Title = !string.IsNullOrEmpty(title) ? title : item.Movie.Title;
        Overview = !string.IsNullOrEmpty(overview) ? overview : item.Movie.Overview;

        Backdrop = item.Movie.Backdrop;
        Logo = item.Movie.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;
        MediaType = "item";
        Year = item.Movie.ReleaseDate.ParseYear();
        ColorPalette = item.Movie.ColorPalette;
        Poster = item.Movie.Poster;
        TitleSort = item.Movie.Title.TitleSort(item.Movie.ReleaseDate);
        Type = "item";
        Link = new($"/movie/{Id}", UriKind.Relative);
        Genres = item.Movie.GenreMovies.Select(genreMovie => new GenreDto(genreMovie)).ToArray();
        VideoId = item.Movie.Video;
        Videos = item
            .Movie.Media.Where(media => media.Site == "YouTube")
            .Select(media => new VideoDto(media))
            .ToArray();
    }

    public SpecialsResponseItemDto(Special special)
    {
        Id = special.Id.ToString();
        Title = special.Title.OrEmpty();
        Overview = special.Overview;
        Backdrop = special.Backdrop;
        Logo = special.Logo;

        MediaType = "specials";
        Link = new($"/specials/{Id}", UriKind.Relative);
        Year = special.CreatedAt.ParseYear();

        ColorPalette = special.ColorPalette;
        Poster = special.Poster;
        TitleSort = special.Title.TitleSort();

        Type = "specials";

        NumberOfItems = special.Items.Count;

        int haveMovies = special
            .Items.Select(item => item.Movie)
            .Count(movie => movie is not null && movie.VideoFiles.Count != 0);

        int haveEpisodes = special
            .Items.Select(item => item.Episode)
            .Count(movie => movie is not null && movie.VideoFiles.Count != 0);

        HaveItems = haveMovies + haveEpisodes;

        int[] movies = special
            .Items.Where(item => item.MovieId is not null)
            .Select(item => item.Movie?.VideoFiles.FirstOrDefault()?.Duration?.ToSeconds() ?? 0)
            .ToArray();

        int[] episodes = special
            .Items.Where(item => item.EpisodeId is not null)
            .Select(item => item.Episode?.VideoFiles.FirstOrDefault()?.Duration?.ToSeconds() ?? 0)
            .ToArray();

        TotalDuration = movies.Sum() + episodes.Sum();
    }
}
