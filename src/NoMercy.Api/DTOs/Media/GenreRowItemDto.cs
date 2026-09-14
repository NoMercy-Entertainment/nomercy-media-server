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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public record GenreRowItemDto
{
    [JsonProperty("id")]
    public dynamic? Id { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("titleSort")]
    public string? TitleSort { get; set; }

    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("media_type")]
    public string? MediaType { get; set; }

    [JsonProperty("genres")]
    public GenreDto[] Genres { get; set; } = [];

    [JsonProperty("tags")]
    public IEnumerable<string> Tags { get; set; } = [];

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("rating")]
    public RatingClass? Rating { get; set; }

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("content_ratings")]
    public IEnumerable<ContentRating> ContentRatings { get; set; } = [];

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    [JsonProperty("videos")]
    public VideoDto[] Videos { get; set; } = [];

    public GenreRowItemDto(Movie movie, string country)
    {
        string? title = movie.Translations.FirstOrDefault()?.Title;
        string? overview = movie.Translations.FirstOrDefault()?.Overview;

        Id = movie.Id;
        Title = title.OrWhenEmpty(movie.Title);
        Overview = overview.OrWhenEmpty(movie.Overview);
        Poster = movie.Poster;
        Backdrop = movie.Backdrop;
        Logo = movie
            .Images.FirstOrDefault(image =>
                image is { Type: "logo", Iso6391: "en" } && image.Width >= image.Height
            )
            ?.FilePath;
        TitleSort = movie.Title.TitleSort(movie.ReleaseDate);
        Year = movie.ReleaseDate.ParseYear();

        MediaType = MediaTypes.MovieMediaType;
        Type = MediaTypes.MovieMediaType;
        Link = new($"/movie/{Id}", UriKind.Relative);
        NumberOfItems = 1;
        HaveItems = movie.VideoFiles.Count(v => v.Folder != null);

        Tags = movie.KeywordMovies.Select(tag => tag.Keyword.Name);

        ColorPalette = movie.ColorPalette;
        Videos = [.. movie.Media.Select(media => new VideoDto(media))];

        ContentRatings = movie
            .CertificationMovies.Where(certificationMovie =>
                RatingClass.IsShownIn(certificationMovie.Certification, country)
            )
            .Select(certificationMovie => ContentRating.From(certificationMovie.Certification));
    }

    public GenreRowItemDto(Tv tv, string country)
    {
        string? title = tv.Translations.FirstOrDefault()?.Title;
        string? overview = tv.Translations.FirstOrDefault()?.Overview;

        Id = tv.Id;
        Title = title.OrWhenEmpty(tv.Title);
        Overview = overview.OrWhenEmpty(tv.Overview);
        Poster = tv.Poster;
        Backdrop = tv.Backdrop;
        Logo = tv
            .Images.FirstOrDefault(image =>
                image is { Type: "logo", Iso6391: "en" } && image.Width >= image.Height
            )
            ?.FilePath;
        TitleSort = tv.Title.TitleSort(tv.FirstAirDate);
        Type = tv.Type;
        Year = tv.FirstAirDate.ParseYear();

        Tags = tv.KeywordTvs.Select(tag => tag.Keyword.Name);

        MediaType = MediaTypes.TvMediaType;
        Type = MediaTypes.TvMediaType;
        Link = new($"/tv/{Id}", UriKind.Relative);
        NumberOfItems = tv.NumberOfEpisodes;
        HaveItems = tv.Episodes.Count(episode => episode.VideoFiles.Any(v => v.Folder != null));

        ColorPalette = tv.ColorPalette;
        Videos = [.. tv.Media.Select(media => new VideoDto(media))];

        ContentRatings = tv
            .CertificationTvs.Where(certificationMovie =>
                RatingClass.IsShownIn(certificationMovie.Certification, country)
            )
            .Select(certificationTv => ContentRating.From(certificationTv.Certification));
    }

    public GenreRowItemDto(HomeMovieCardDto movie)
    {
        Id = movie.Id;
        Title = !string.IsNullOrEmpty(movie.TranslatedTitle) ? movie.TranslatedTitle : movie.Title;
        Overview = !string.IsNullOrEmpty(movie.TranslatedOverview)
            ? movie.TranslatedOverview
            : movie.Overview;
        Poster = movie.Poster;
        Backdrop = movie.Backdrop;
        Logo = movie.Logo;
        TitleSort = movie.TitleSort;
        Year = movie.ReleaseDate.ParseYear();
        MediaType = MediaTypes.MovieMediaType;
        Type = MediaTypes.MovieMediaType;
        Link = new($"/movie/{movie.Id}", UriKind.Relative);
        NumberOfItems = 1;
        HaveItems = movie.VideoFileCount;
        Tags = [];
        Videos = [];

        ColorPalette = ColorPalette.FromJsonOrNull(movie.ColorPalette);

        if (movie.CertificationRating != null)
        {
            ContentRatings =
            [
                new()
                {
                    Rating = movie.CertificationRating,
                    Iso31661 = movie.CertificationCountry!,
                },
            ];
        }
    }

    public GenreRowItemDto(HomeTvCardDto tv)
    {
        Id = tv.Id;
        Title = !string.IsNullOrEmpty(tv.TranslatedTitle) ? tv.TranslatedTitle : tv.Title;
        Overview = !string.IsNullOrEmpty(tv.TranslatedOverview)
            ? tv.TranslatedOverview
            : tv.Overview;
        Poster = tv.Poster;
        Backdrop = tv.Backdrop;
        Logo = tv.Logo;
        TitleSort = tv.TitleSort;
        Year = tv.FirstAirDate.ParseYear();
        MediaType = MediaTypes.TvMediaType;
        Type = MediaTypes.TvMediaType;
        Link = new($"/tv/{tv.Id}", UriKind.Relative);
        NumberOfItems = tv.NumberOfEpisodes;
        HaveItems = tv.EpisodesWithVideo;
        Tags = [];
        Videos = [];

        ColorPalette = ColorPalette.FromJsonOrNull(tv.ColorPalette);

        if (tv.CertificationRating != null)
        {
            ContentRatings =
            [
                new() { Rating = tv.CertificationRating, Iso31661 = tv.CertificationCountry! },
            ];
        }
    }

    public GenreRowItemDto()
    {
        //
    }

    public GenreRowItemDto(Collection collection, string country)
    {
        string? title = collection.Translations.FirstOrDefault()?.Title;
        string? overview = collection.Translations.FirstOrDefault()?.Overview;

        Id = collection.Id;
        Title = title.OrWhenEmpty(collection.Title);
        Overview = overview.OrWhenEmpty(collection.Overview);
        Poster = collection.Poster;
        Backdrop = collection.Backdrop;
        Logo = collection
            .Images.FirstOrDefault(image =>
                image is { Type: "logo", Iso6391: "en" } && image.Width >= image.Height
            )
            ?.FilePath;
        TitleSort = collection.Title.TitleSort(
            collection.CollectionMovies.MinBy(movie => movie.Movie.ReleaseDate)?.Movie.ReleaseDate
        );
        Type = MediaTypes.CollectionMediaType;
        Year = collection
            .CollectionMovies.MinBy(movie => movie.Movie.ReleaseDate)
            ?.Movie.ReleaseDate.ParseYear();

        MediaType = MediaTypes.TvMediaType;
        Type = MediaTypes.TvMediaType;
        Link = new($"/collection/{Id}", UriKind.Relative);
        NumberOfItems = collection.CollectionMovies.Count;
        HaveItems = collection.CollectionMovies.Count(movie =>
            movie.Movie.VideoFiles.Any(v => v.Folder != null)
        );

        Tags = [];

        ColorPalette = collection.ColorPalette;

        ContentRatings = collection
            .CollectionMovies.SelectMany(collectionMovie =>
                collectionMovie.Movie.CertificationMovies
            )
            .Where(certificationMovie =>
                RatingClass.IsShownIn(certificationMovie.Certification, country)
            )
            .Select(certificationMovie => ContentRating.From(certificationMovie.Certification));
    }

    public GenreRowItemDto(Special special, string country)
    {
        Id = special.Id;
        Title = special.Title;
        Overview = special.Overview;
        Poster = special.Poster;
        Backdrop = special.Backdrop;
        Logo = special.Logo;
        TitleSort = special.Title.TitleSort();
        Type = MediaTypes.CollectionMediaType;
        Year =
            special.Items.MinBy(movie => movie.Movie?.ReleaseDate)?.Movie?.ReleaseDate.ParseYear()
            ?? special
                .Items.Select(tv => tv.Episode?.Tv)
                .FirstOrDefault()
                ?.FirstAirDate.ParseYear();

        MediaType = MediaTypes.TvMediaType;
        Type = MediaTypes.TvMediaType;
        Link = new($"/specials/{Id}", UriKind.Relative);

        NumberOfItems = special.Items.Count;

        Tags = [];

        int haveMovies = special
            .Items.Select(item => item.Movie)
            .Count(movie => movie is not null && movie.VideoFiles.Count != 0);

        int haveEpisodes = special
            .Items.Select(item => item.Episode)
            .Count(movie => movie is not null && movie.VideoFiles.Count != 0);

        HaveItems = haveMovies + haveEpisodes;

        ColorPalette = special.ColorPalette;

        ContentRatings = special
            .Items.SelectMany(item =>
                item.Movie?.CertificationMovies ?? Enumerable.Empty<CertificationMovie>()
            )
            .Where(certificationMovie =>
                RatingClass.IsShownIn(certificationMovie.Certification, country)
            )
            .Select(certificationMovie => ContentRating.From(certificationMovie.Certification));
    }
}
