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
using NoMercy.Providers.TMDB.Models.Collections;

namespace NoMercy.Api.DTOs.Media;

public record CollectionResponseItemDto
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("titleSort")]
    public string? TitleSort { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonProperty("duration")]
    public int Duration { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("collection")]
    public CollectionMovieDto[] Collection { get; set; } = [];

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("watched")]
    public bool Watched { get; set; }

    [JsonProperty("genres")]
    public GenreDto[] Genres { get; set; } = [];

    [JsonProperty("total_duration")]
    public int TotalDuration { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    [JsonProperty("keywords")]
    public IEnumerable<string> Keywords { get; set; } = [];

    [JsonProperty("cast")]
    public PeopleDto[] Cast { get; set; } = [];

    [JsonProperty("crew")]
    public PeopleDto[] Crew { get; set; } = [];

    [JsonProperty("backdrops")]
    public ImageDto[] Backdrops { get; set; } = [];

    [JsonProperty("posters")]
    public ImageDto[] Posters { get; set; } = [];

    [JsonProperty("content_ratings")]
    public ContentRating[] ContentRatings { get; set; } = [];

    [JsonProperty("vote_average")]
    public double VoteAverage { get; set; }

    public CollectionResponseItemDto(Collection? collection)
    {
        if (collection is null)
            return;

        string? title = collection.Translations.FirstOrDefault()?.Title;
        string? overview = collection.Translations.FirstOrDefault()?.Overview;

        Id = collection.Id;
        Title = title.OrWhenEmpty(collection.Title);
        Overview = overview.OrWhenEmpty(collection.Overview);
        Backdrop = collection.Backdrop;
        Poster = collection.Poster;
        TitleSort = collection.TitleSort;

        Type = MediaTypes.CollectionMediaType;
        MediaType = MediaTypes.CollectionMediaType;
        Link = new($"/collection/{Id}", UriKind.Relative);

        ColorPalette = collection.ColorPalette;
        NumberOfItems = collection.Parts;
        HaveItems = collection.CollectionMovies.Count(collectionMovie =>
            collectionMovie.Movie.VideoFiles.Count > 0
        );

        TotalDuration = collection.CollectionMovies.Sum(item => item.Movie.Runtime * 60 ?? 0);

        Favorite = collection.CollectionUser.Count != 0;
        Watched =
            collection.CollectionMovies.Count(collectionMovie =>
                collectionMovie.Movie.MovieUser.Count != 0
            ) == collection.CollectionMovies.Count;

        Duration = (int)
            collection
                .CollectionMovies.Select(movie =>
                    movie.Movie.VideoFiles.FirstOrDefault()?.Duration?.ToSeconds()
                    ?? movie.Movie.Runtime * 60
                    ?? 0
                )
                .Average();

        VoteAverage =
            collection
                .CollectionMovies.Where(movie => movie.Movie.VoteAverage != null)
                .Select(movie => movie.Movie.VoteAverage)
                .Average()
            ?? 0;

        Keywords = collection
            .CollectionMovies.SelectMany(movie => movie.Movie.KeywordMovies)
            .DistinctBy(keyword => keyword.KeywordId)
            .Select(keywordMovie => keywordMovie.Keyword.Name)
            .OrderBy(keyword => keyword)
            .ToArray();

        Genres = collection
            .CollectionMovies.SelectMany(movie => movie.Movie.GenreMovies)
            .DistinctBy(genreMovie => genreMovie.GenreId)
            .Select(genreMovie => new GenreDto(genreMovie))
            .ToArray();

        ContentRatings = collection
            .CollectionMovies.SelectMany(collectionMovie =>
                collectionMovie.Movie.CertificationMovies
            )
            .DistinctBy(certification => certification.Certification.Iso31661)
            .Select(certificationMovie => ContentRating.From(certificationMovie.Certification))
            .ToArray();

        Collection = collection
            .CollectionMovies.OrderBy(movie => movie.Movie.ReleaseDate)
            .Select(movie => new CollectionMovieDto(movie.Movie))
            .ToArray();

        Backdrops = collection
            .CollectionMovies.SelectMany(movie => movie.Movie.Images)
            .Where(media => media.Type == "backdrop")
            .Select(media => new ImageDto(media))
            .OrderByDescending(image => image.VoteAverage)
            .ToArray();

        Posters = collection
            .CollectionMovies.SelectMany(movie => movie.Movie.Images)
            .Where(media => media.Type == "poster")
            .Select(media => new ImageDto(media))
            .OrderByDescending(image => image.VoteAverage)
            .ToArray();

        Cast = collection
            .CollectionMovies.SelectMany(movie => movie.Movie.Cast)
            .Select(cast => new PeopleDto(cast))
            .OrderBy(cast => cast.Order)
            .DistinctBy(people => people.Id)
            .ToArray();

        Crew = collection
            .CollectionMovies.SelectMany(movie => movie.Movie.Crew)
            .Select(crew => new PeopleDto(crew))
            .OrderBy(crew => crew.Order)
            .DistinctBy(people => people.Id)
            .ToArray();
    }

    public CollectionResponseItemDto(TmdbCollectionAppends tmdbCollectionAppends)
    {
        string? title = tmdbCollectionAppends
            .Translations.Translations.FirstOrDefault()
            ?.Data.Title;
        string? overview = tmdbCollectionAppends
            .Translations.Translations.FirstOrDefault()
            ?.Data.Overview;

        Id = tmdbCollectionAppends.Id;
        Title = title.OrWhenEmpty(tmdbCollectionAppends.Name);
        Overview = overview.OrWhenEmpty(tmdbCollectionAppends.Overview);
        Backdrop = tmdbCollectionAppends.BackdropPath;
        Poster = tmdbCollectionAppends.PosterPath;
        TitleSort = tmdbCollectionAppends.Name.TitleSort();
        Type = MediaTypes.CollectionMediaType;
        MediaType = MediaTypes.CollectionMediaType;
        ColorPalette = new();
        NumberOfItems = tmdbCollectionAppends.Parts.Length;
        HaveItems = 0;
        Favorite = false;
        Link = new($"/collection/{Id}", UriKind.Relative);

        VoteAverage =
            tmdbCollectionAppends
                .Parts.Where(movie => movie.VoteAverage > 0)
                .Average(movie => (double?)movie.VoteAverage)
            ?? 0;

        Keywords = [];

        Genres = [];
        Cast = [];
        Crew = [];

        Collection = tmdbCollectionAppends
            .Parts.OrderBy(item => item.TitleSort())
            .Select(movie => new CollectionMovieDto(movie))
            .ToArray();

        Backdrops = tmdbCollectionAppends
            .Images.Backdrops.Select(media => new ImageDto(media))
            .OrderByDescending(image => image.VoteAverage)
            .ToArray();
        Posters = tmdbCollectionAppends
            .Images.Posters.Select(media => new ImageDto(media))
            .OrderByDescending(image => image.VoteAverage)
            .ToArray();
    }
}
