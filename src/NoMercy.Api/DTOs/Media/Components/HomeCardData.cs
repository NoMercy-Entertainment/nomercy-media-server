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

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Data for NMHomeCard component - featured home page card with video trailer support.
/// </summary>
public record HomeCardData
{
    [JsonProperty("id")]
    public dynamic? Id { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    [JsonProperty("rating", NullValueHandling = NullValueHandling.Ignore)]
    public RatingClass? Rating { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("duration")]
    public int? Duration { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    /// <summary>
    /// Whether <see cref="Poster" /> is one of TMDB's language-less prints, and so carries no
    /// title of its own. A surface that leads with the poster puts the logo over it when there
    /// is one and draws the title when there is not; over a poster that already reads its own
    /// name it draws neither.
    /// </summary>
    [JsonProperty("poster_is_textless")]
    public bool PosterIsTextless { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    [JsonProperty("media_type")]
    public string? MediaType { get; set; }

    [JsonProperty("videos")]
    public IEnumerable<VideoInfo> Videos { get; set; } = [];

    [JsonProperty("videoID")]
    public string? VideoId { get; set; }

    public HomeCardData() { }

    public HomeCardData(Movie movie, string country)
    {
        string? title = movie.Translations.FirstOrDefault()?.Title;
        string? overview = movie.Translations.FirstOrDefault()?.Overview;

        Id = movie.Id;
        Title = title.OrWhenEmpty(movie.Title);
        Overview = overview.OrWhenEmpty(movie.Overview);
        Poster = movie.Poster;
        Backdrop = movie.Backdrop;
        Logo = movie.Images.FirstOrDefault(i => i.Type == "logo")?.FilePath;
        Year = movie.ReleaseDate.ParseYear();
        MediaType = "movie";
        Link = new($"/movie/{Id}", UriKind.Relative);
        NumberOfItems = 1;
        HaveItems = movie.VideoFiles.Count(v => v.Folder != null);
        ColorPalette = movie.ColorPalette;

        Videos = movie
            .Media.Where(m => m.Site == "YouTube")
            .Select(m => new VideoInfo
            {
                Id = m.Src,
                Name = m.Name,
                Site = m.Site,
                Type = m.Type,
            });
        VideoId = Videos.FirstOrDefault()?.Id;

        Rating = movie
            .CertificationMovies.Where(cm => RatingClass.IsShownIn(cm.Certification, country))
            .Select(cm => RatingClass.From(cm.Certification))
            .FirstOrDefault();
    }

    public HomeCardData(Tv tv, string country)
    {
        string? title = tv.Translations.FirstOrDefault()?.Title;
        string? overview = tv.Translations.FirstOrDefault()?.Overview;

        Id = tv.Id;
        Title = title.OrWhenEmpty(tv.Title);
        Overview = overview.OrWhenEmpty(tv.Overview);
        Poster = tv.Poster;
        Backdrop = tv.Backdrop;
        Logo = tv.Images.FirstOrDefault(i => i.Type == "logo")?.FilePath;
        Year = tv.FirstAirDate.ParseYear();
        MediaType = "tv";
        Link = new($"/tv/{Id}", UriKind.Relative);
        NumberOfItems = tv.NumberOfEpisodes;
        HaveItems = tv.Episodes.Count(episode => episode.VideoFiles.Any(v => v.Folder != null));
        ColorPalette = tv.ColorPalette;

        Videos = tv
            .Media.Where(m => m.Site == "YouTube")
            .Select(m => new VideoInfo
            {
                Id = m.Src,
                Name = m.Name,
                Site = m.Site,
                Type = m.Type,
            });
        VideoId = Videos.FirstOrDefault()?.Id;

        Rating = tv
            .CertificationTvs.Where(ct => RatingClass.IsShownIn(ct.Certification, country))
            .Select(ct => RatingClass.From(ct.Certification))
            .FirstOrDefault();
    }

    public HomeCardData(NmCardDto cardDto)
    {
        Id = cardDto.Id;
        Title = cardDto.Title;
        Overview = cardDto.Overview;
        Poster = cardDto.Poster;
        Backdrop = cardDto.Backdrop;
        Logo = cardDto.Logo;
        Year = cardDto.Year;
        Duration = cardDto.Duration;
        Link = cardDto.Link;
        Rating = cardDto.Rating;
        ColorPalette = cardDto.ColorPalette;
        HaveItems = cardDto.HaveItems;
        NumberOfItems = cardDto.NumberOfItems;
        MediaType = cardDto.Type;
    }
}
