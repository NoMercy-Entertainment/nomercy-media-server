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
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public class NmGenreCardDto
{
    [JsonProperty("id")]
    public dynamic? Id { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("titleSort")]
    public string? TitleSort { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    [JsonProperty("rating")]
    public RatingClass? Rating { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("content_ratings")]
    public IEnumerable<ContentRating> ContentRatings { get; set; } = [];

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    public NmGenreCardDto()
    {
        //
    }

    public NmGenreCardDto(Movie movie, string country)
    {
        string? title = movie.Translations.FirstOrDefault()?.Title;
        string? overview = movie.Translations.FirstOrDefault()?.Overview;

        Id = movie.Id;
        Title = !string.IsNullOrEmpty(title) ? title : movie.Title;
        Overview = !string.IsNullOrEmpty(overview) ? overview : movie.Overview;
        Poster = movie.Poster;
        Backdrop = movie.Backdrop;
        Logo = movie.Images.FirstOrDefault(image => image.Type == "logo")?.FilePath;
        TitleSort = movie.Title.TitleSort(movie.ReleaseDate);
        Year = movie.ReleaseDate.ParseYear();

        Type = "genre";
        Link = new($"/movie/{Id}", UriKind.Relative);
        NumberOfItems = 1;
        HaveItems = movie.VideoFiles.Count(v => v.Folder != null);

        ColorPalette = movie.ColorPalette;

        ContentRatings = movie
            .CertificationMovies.Where(certificationMovie =>
                certificationMovie.Certification.Iso31661 == "US"
                || certificationMovie.Certification.Iso31661 == country
            )
            .Select(certificationMovie => new ContentRating
            {
                Rating = certificationMovie.Certification.Rating,
                Iso31661 = certificationMovie.Certification.Iso31661,
            });
    }

    public NmGenreCardDto(Tv tv, string country)
    {
        string? title = tv.Translations.FirstOrDefault()?.Title;
        string? overview = tv.Translations.FirstOrDefault()?.Overview;

        Id = tv.Id;
        Title = !string.IsNullOrEmpty(title) ? title : tv.Title;
        Overview = !string.IsNullOrEmpty(overview) ? overview : tv.Overview;
        Poster = tv.Poster;
        Backdrop = tv.Backdrop;
        Logo = tv.Images.FirstOrDefault(image => image.Type == "logo")?.FilePath;
        TitleSort = tv.Title.TitleSort(tv.FirstAirDate);
        Year = tv.FirstAirDate.ParseYear();

        Type = "genre";
        Link = new($"/tv/{Id}", UriKind.Relative);
        NumberOfItems = tv.NumberOfEpisodes;
        HaveItems = tv.Episodes.Count(episode => episode.VideoFiles.Any(v => v.Folder != null));

        ColorPalette = tv.ColorPalette;

        ContentRatings = tv
            .CertificationTvs.Where(certificationMovie =>
                certificationMovie.Certification.Iso31661 == "US"
                || certificationMovie.Certification.Iso31661 == country
            )
            .Select(certificationTv => new ContentRating
            {
                Rating = certificationTv.Certification.Rating,
                Iso31661 = certificationTv.Certification.Iso31661,
            });
    }

    public NmGenreCardDto(Collection collection, string country)
    {
        string? title = collection.Translations.FirstOrDefault()?.Title;
        string? overview = collection.Translations.FirstOrDefault()?.Overview;

        Id = collection.Id;
        Title = !string.IsNullOrEmpty(title) ? title : collection.Title;
        Overview = !string.IsNullOrEmpty(overview) ? overview : collection.Overview;
        Poster = collection.Poster;
        Backdrop = collection.Backdrop;
        Logo = collection.Images.FirstOrDefault(image => image.Type == "logo")?.FilePath;
        TitleSort = collection.Title.TitleSort(
            collection.CollectionMovies.MinBy(movie => movie.Movie.ReleaseDate)?.Movie.ReleaseDate
        );
        Year = collection
            .CollectionMovies.MinBy(movie => movie.Movie.ReleaseDate)
            ?.Movie.ReleaseDate.ParseYear();

        Type = "genre";
        Link = new($"/collection/{Id}", UriKind.Relative);
        NumberOfItems = collection.CollectionMovies.Count;
        HaveItems = collection.CollectionMovies.Count(movie =>
            movie.Movie.VideoFiles.Any(v => v.Folder != null)
        );

        ColorPalette = collection.ColorPalette;

        ContentRatings = collection
            .CollectionMovies.SelectMany(collectionMovie =>
                collectionMovie.Movie.CertificationMovies
            )
            .Where(certificationMovie =>
                certificationMovie.Certification.Iso31661 == "US"
                || certificationMovie.Certification.Iso31661 == country
            )
            .Select(certificationMovie => new ContentRating
            {
                Rating = certificationMovie.Certification.Rating,
                Iso31661 = certificationMovie.Certification.Iso31661,
            });
    }

    public NmGenreCardDto(Special special, string country)
    {
        Id = special.Id;
        Title = special.Title;
        Overview = special.Overview;
        Poster = special.Poster;
        Backdrop = special.Backdrop;
        Logo = special.Logo;
        TitleSort = special.Title.TitleSort();
        Year =
            special.Items.MinBy(movie => movie.Movie?.ReleaseDate)?.Movie?.ReleaseDate.ParseYear()
            ?? special
                .Items.Select(tv => tv.Episode?.Tv)
                .FirstOrDefault()
                ?.FirstAirDate.ParseYear();

        Type = "genre";
        Link = new($"/specials/{Id}", UriKind.Relative);

        NumberOfItems = special.Items.Count;

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
                certificationMovie.Certification.Iso31661 == "US"
                || certificationMovie.Certification.Iso31661 == country
            )
            .Select(certificationMovie => new ContentRating
            {
                Rating = certificationMovie.Certification.Rating,
                Iso31661 = certificationMovie.Certification.Iso31661,
            });
    }

    public NmGenreCardDto(Genre genre)
    {
        Id = genre.Id;
        Title = genre.Name.ToTitleCase();
        TitleSort = genre.Name;

        Type = "genre";
        Link = new($"/genres/{genre.Id}", UriKind.Relative);
        NumberOfItems = genre.GenreMovies.Count + genre.GenreTvShows.Count;
        HaveItems =
            genre.GenreMovies.Count(genreMovie =>
                genreMovie.Movie.VideoFiles.Any(v => v.Folder != null)
            )
            + genre.GenreTvShows.Count(genreTv =>
                genreTv.Tv.Episodes.Any(episode => episode.VideoFiles.Any(v => v.Folder != null))
            );
    }

    public NmGenreCardDto(MusicGenre genre)
    {
        Id = genre.Id;
        Title = genre.Name.ToTitleCase();
        TitleSort = genre.Name.TitleSort();

        Type = "genre";
        Link = new($"/music/genres/{genre.Id}", UriKind.Relative);
        NumberOfItems = genre.MusicGenreTracks.Count;
        HaveItems = genre.MusicGenreTracks.Count;
    }

    public NmGenreCardDto(MusicGenreCardDto genre)
    {
        Id = genre.Id;
        Title = genre.Name.ToTitleCase();
        TitleSort = genre.Name.TitleSort();

        Type = "genre";
        Link = new($"/music/genres/{genre.Id}", UriKind.Relative);
        NumberOfItems = genre.TrackCount;
        HaveItems = genre.TrackCount;
    }
}
