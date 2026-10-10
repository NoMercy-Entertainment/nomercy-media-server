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
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.People;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public record LibraryResponseItemDto
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("watched")]
    public bool Watched { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("media_type")]
    public string MediaType { get; set; }

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("titleSort")]
    public string? TitleSort { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("videoId")]
    public string? VideoId { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("genres")]
    public GenreDto[]? Genres { get; set; }

    [JsonProperty("videos")]
    public VideoDto[] Videos { get; set; } = [];

    public LibraryResponseItemDto(LibraryMovie movie)
    {
        Id = movie.Movie.Id.ToString();
        Backdrop = movie.Movie.Backdrop;
        Logo = movie.Movie.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;
        MediaType = MediaTypes.MovieMediaType;
        Year = movie.Movie.ReleaseDate.ParseYear();
        Overview = movie.Movie.Overview;
        ColorPalette = movie.Movie.ColorPalette;
        Poster = movie.Movie.Poster;
        Title = movie.Movie.Title;
        TitleSort = movie.Movie.Title.TitleSort(movie.Movie.ReleaseDate);
        Type = MediaTypes.MovieMediaType;
        Link = new($"/movie/{Id}", UriKind.Relative);
        Genres = movie.Movie.GenreMovies.Select(genreMovie => new GenreDto(genreMovie)).ToArray();
        VideoId = movie.Movie.Video;
        Videos = movie
            .Movie.Media.Where(media => media.Site == "YouTube")
            .Select(media => new VideoDto(media))
            .ToArray();
    }

    public LibraryResponseItemDto(LibraryTv tv)
    {
        Id = tv.Tv.Id.ToString();
        Backdrop = tv.Tv.Backdrop;
        Logo = tv.Tv.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;
        Year = tv.Tv.FirstAirDate.ParseYear();
        Overview = tv.Tv.Overview;
        ColorPalette = tv.Tv.ColorPalette;
        Poster = tv.Tv.Poster;
        Title = tv.Tv.Title;
        TitleSort = tv.Tv.Title.TitleSort(tv.Tv.FirstAirDate);

        Type = MediaTypes.TvMediaType;
        MediaType = MediaTypes.TvMediaType;
        Link = new($"/tv/{Id}", UriKind.Relative);
        NumberOfItems = tv.Tv.NumberOfEpisodes;
        HaveItems = tv.Tv.Episodes.Count(episode => episode.VideoFiles.Any(v => v.Folder != null));

        Genres = tv.Tv.GenreTvs.Select(genreTv => new GenreDto(genreTv)).ToArray();
        VideoId = tv.Tv.Trailer;
        Videos = tv
            .Tv.Media.Where(media => media.Site == "YouTube")
            .Select(media => new VideoDto(media))
            .ToArray();
    }

    public LibraryResponseItemDto(Movie movie)
    {
        Id = movie.Id.ToString();
        Backdrop = movie.Backdrop;
        Logo = movie.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;
        MediaType = MediaTypes.MovieMediaType;
        Year = movie.ReleaseDate.ParseYear();
        Overview = movie.Overview;
        ColorPalette = movie.ColorPalette;
        Poster = movie.Poster;
        Title = movie.Title;
        TitleSort = movie.Title.TitleSort(movie.ReleaseDate);
        Type = MediaTypes.MovieMediaType;
        Link = new($"/movie/{Id}", UriKind.Relative);
        HaveItems = movie.VideoFiles.Count(v => v.Folder != null);
        NumberOfItems = 1;

        Genres = movie.GenreMovies.Select(genreMovie => new GenreDto(genreMovie)).ToArray();
        VideoId = movie.Video;
        Videos = movie
            .Media.Where(media => media.Site == "YouTube")
            .Select(media => new VideoDto(media))
            .ToArray();
    }

    public LibraryResponseItemDto(Tv tv)
    {
        Id = tv.Id.ToString();
        Backdrop = tv.Backdrop;
        Logo = tv.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;
        Year = tv.FirstAirDate.ParseYear();
        Overview = tv.Overview;
        ColorPalette = tv.ColorPalette;
        Poster = tv.Poster;
        Title = tv.Title;
        TitleSort = tv.Title.TitleSort(tv.FirstAirDate);

        Type = MediaTypes.TvMediaType;
        MediaType = MediaTypes.TvMediaType;
        Link = new($"/tv/{Id}", UriKind.Relative);

        NumberOfItems = tv.NumberOfEpisodes;
        HaveItems = tv.Episodes.Count(episode => episode.VideoFiles.Any(v => v.Folder != null));

        Genres = tv.GenreTvs.Select(genreTv => new GenreDto(genreTv)).ToArray();
        VideoId = tv.Trailer;
        Videos = tv
            .Media.Where(media => media.Site == "YouTube")
            .Select(media => new VideoDto(media))
            .ToArray();
    }

    public LibraryResponseItemDto(CollectionMovie movie)
    {
        Id = movie.Movie.Id.ToString();
        Backdrop = movie.Movie.Backdrop;
        Logo = movie.Movie.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;
        MediaType = MediaTypes.MovieMediaType;
        Year = movie.Movie.ReleaseDate.ParseYear();
        Overview = movie.Movie.Overview;
        ColorPalette = movie.Movie.ColorPalette;
        Poster = movie.Movie.Poster;
        Title = movie.Movie.Title;
        TitleSort = movie.Movie.Title.TitleSort(movie.Movie.ReleaseDate);
        Type = MediaTypes.MovieMediaType;
        Link = new($"/movie/{Id}", UriKind.Relative);
        HaveItems = movie.Movie.VideoFiles.Count(v => v.Folder != null);
        NumberOfItems = 1;

        Genres = movie.Movie.GenreMovies.Select(genreMovie => new GenreDto(genreMovie)).ToArray();
        VideoId = movie.Movie.Video;
        Videos = movie
            .Movie.Media.Where(media => media.Site == "YouTube")
            .Select(media => new VideoDto(media))
            .ToArray();
    }

    public LibraryResponseItemDto(Collection collection)
    {
        string title = collection.Translations.FirstOrDefault()?.Title ?? collection.Title;

        string overview = (
            collection.Translations.FirstOrDefault()?.Overview ?? collection.Overview
        ).OrEmpty();

        Id = collection.Id.ToString();
        Title = CollectionTitleFormatter.StripCollectionSuffix(title);
        Overview = overview;
        Backdrop = collection.Backdrop;
        Logo = collection.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;

        Year = collection
            .CollectionMovies.MinBy(collectionMovie => collectionMovie.Movie.ReleaseDate)
            ?.Movie.ReleaseDate.ParseYear();

        ColorPalette = collection.ColorPalette;
        Poster = collection.Poster;
        TitleSort = collection.Title.TitleSort(
            collection
                .CollectionMovies.MinBy(collectionMovie => collectionMovie.Movie.ReleaseDate)
                ?.Movie.ReleaseDate.ParseYear()
        );

        Type = "specials";
        MediaType = "specials";
        Link = new($"/collection/{Id}", UriKind.Relative);

        NumberOfItems = collection.Parts;
        HaveItems = collection.CollectionMovies.Count(collectionMovie =>
            collectionMovie.Movie.VideoFiles.Any(v => v.Folder != null)
        );

        Genres = collection
            .CollectionMovies.Select(genreTv => genreTv.Movie)
            .SelectMany(movie => movie.GenreMovies.Select(genreMovie => genreMovie.Genre))
            .Select(genre => new GenreDto(genre))
            .ToArray();

        VideoId = collection.CollectionMovies.FirstOrDefault()?.Movie.Video;
    }

    public LibraryResponseItemDto(Special special)
    {
        string title = special.Title.OrEmpty();

        string overview = special.Overview.OrEmpty();

        Id = special.Id.ToString();
        Name = title;
        Overview = overview;
        Backdrop = special.Backdrop;
        MediaType = "specials";
        Link = new($"/specials/{Id}", UriKind.Relative);

        ColorPalette = special.ColorPalette;
        Poster = special.Poster;
        TitleSort = special.Title.TitleSort();

        Type = "specials";
    }

    public LibraryResponseItemDto(Person person)
    {
        string name = person.Translations.FirstOrDefault()?.Title ?? person.Name;

        string biography = (
            person.Translations.FirstOrDefault()?.Biography ?? person.Biography
        ).OrEmpty();

        Id = person.Id.ToString();
        Name = name;
        Overview = biography;

        MediaType = "person";
        Type = "person";
        Link = new($"/person/{Id}", UriKind.Relative);

        TitleSort = person.Name.TitleSort();
        ColorPalette = person.ColorPalette;
        Poster = person.Profile;
    }
}
