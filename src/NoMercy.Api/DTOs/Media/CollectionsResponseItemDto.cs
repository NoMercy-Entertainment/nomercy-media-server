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
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public record CollectionsResponseItemDto
{
    [JsonProperty("id")]
    public long Id { get; set; }

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

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; }

    [JsonProperty("titleSort")]
    public string? TitleSort { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("genres")]
    public GenreDto[]? Genres { get; set; }

    [JsonProperty("videoId")]
    public string? VideoId { get; set; }

    [JsonProperty("videos")]
    public VideoDto[]? Videos { get; set; } = [];

    public CollectionsResponseItemDto(CollectionMovie collectionMovie)
    {
        string? title = collectionMovie.Movie.Translations.FirstOrDefault()?.Title;
        string? overview = collectionMovie.Movie.Translations.FirstOrDefault()?.Overview;

        Id = collectionMovie.Movie.Id;
        Title = title.OrWhenEmpty(collectionMovie.Movie.Title);
        Overview = overview.OrWhenEmpty(collectionMovie.Movie.Overview);

        Backdrop = collectionMovie.Movie.Backdrop;
        Logo = collectionMovie.Movie.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;
        MediaType = "collectionMovie";
        Link = new($"/movie/{Id}", UriKind.Relative);
        Year = collectionMovie.Movie.ReleaseDate.ParseYear();
        ColorPalette = collectionMovie.Movie.ColorPalette;
        Poster = collectionMovie.Movie.Poster;
        TitleSort = collectionMovie.Movie.Title.TitleSort(collectionMovie.Movie.ReleaseDate);
        Type = "collectionMovie";
        Genres =
        [
            .. collectionMovie.Movie.GenreMovies.Select(genreMovie => new GenreDto(genreMovie)),
        ];
        VideoId = collectionMovie.Movie.Video;
        Videos =
        [
            .. collectionMovie
                .Movie.Media.Where(media => media.Site == "YouTube")
                .Select(media => new VideoDto(media)),
        ];
    }

    public CollectionsResponseItemDto(Collection collection)
    {
        string? title = collection.Translations.FirstOrDefault()?.Title;
        string? overview = collection.Translations.FirstOrDefault()?.Overview;

        Id = collection.Id;
        Title = title.OrWhenEmpty(collection.Title);
        Overview = overview.OrWhenEmpty(collection.Overview);
        Backdrop = collection.Backdrop;
        Logo = collection.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;
        Link = new($"/collection/{Id}", UriKind.Relative);
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

        MediaType = MediaTypes.CollectionMediaType;
        Type = MediaTypes.CollectionMediaType;

        NumberOfItems = collection.Parts;
        HaveItems = collection.CollectionMovies.Count(collectionMovie =>
            collectionMovie.Movie.VideoFiles.Any(v => v.Folder != null)
        );

        Genres =
        [
            .. collection
                .CollectionMovies.Select(genreTv => genreTv.Movie)
                .SelectMany(movie => movie.GenreMovies.Select(genreMovie => genreMovie.Genre))
                .Select(genre => new GenreDto(genre)),
        ];

        VideoId = collection.CollectionMovies.FirstOrDefault()?.Movie.Video;
    }
}
