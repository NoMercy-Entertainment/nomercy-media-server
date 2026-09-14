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
using NoMercy.Data.DTOs.Specials;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Data for NMCard component - standard media card showing movies, TV shows, collections, etc.
/// </summary>
public record CardData
{
    [JsonProperty("id")]
    public dynamic? Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("titleSort")]
    public string TitleSort { get; set; } = string.Empty;

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

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    public CardData() { }

    public CardData(Movie movie, string country, bool watch = false)
    {
        string? title = movie.Translations.FirstOrDefault()?.Title;
        string? overview = movie.Translations.FirstOrDefault()?.Overview;

        Id = movie.Id;
        Title = !string.IsNullOrEmpty(title) ? title : movie.Title;
        Overview = !string.IsNullOrEmpty(overview) ? overview : movie.Overview;
        Poster = movie.Poster;
        Backdrop = movie.Backdrop;
        Logo = movie.Images.FirstOrDefault(i => i.Type == "logo")?.FilePath;
        TitleSort = movie.Title.TitleSort(movie.ReleaseDate);
        Year = movie.ReleaseDate.ParseYear();
        Type = MediaTypes.MovieMediaType;
        Link = watch
            ? new($"/movie/{Id}/watch", UriKind.Relative)
            : new($"/movie/{Id}", UriKind.Relative);
        NumberOfItems = 1;
        HaveItems = movie.VideoFiles.Count(v => v.Folder != null);
        ColorPalette = movie.ColorPalette;
        CreatedAt = movie.CreatedAt;

        Rating = movie
            .CertificationMovies.Where(cm =>
                cm.Certification.Iso31661 == "US" || cm.Certification.Iso31661 == country
            )
            .Select(cm => new RatingClass
            {
                Rating = cm.Certification.Rating,
                Iso31661 = cm.Certification.Iso31661,
                Image = new(
                    $"/{cm.Certification.Iso31661}/{cm.Certification.Iso31661}_{cm.Certification.Rating}.svg"
                ),
            })
            .FirstOrDefault();
    }

    public CardData(Tv tv, string country, bool watch = false)
    {
        string? title = tv.Translations.FirstOrDefault()?.Title;
        string? overview = tv.Translations.FirstOrDefault()?.Overview;

        Id = tv.Id;
        Title = !string.IsNullOrEmpty(title) ? title : tv.Title;
        Overview = !string.IsNullOrEmpty(overview) ? overview : tv.Overview;
        Poster = tv.Poster;
        Backdrop = tv.Backdrop;
        Logo = tv.Images.FirstOrDefault(i => i.Type == "logo")?.FilePath;
        TitleSort = tv.Title.TitleSort(tv.FirstAirDate);
        Year = tv.FirstAirDate.ParseYear();
        Type = "tv";
        CreatedAt = tv.CreatedAt;
        Link = watch
            ? new($"/tv/{Id}/watch", UriKind.Relative)
            : new($"/tv/{Id}", UriKind.Relative);
        NumberOfItems = tv.NumberOfEpisodes;
        HaveItems = tv.Episodes.Count(episode => episode.VideoFiles.Any(v => v.Folder != null));
        ColorPalette = tv.ColorPalette;

        Rating = tv
            .CertificationTvs.Where(ct =>
                ct.Certification.Iso31661 == "US" || ct.Certification.Iso31661 == country
            )
            .Select(ct => new RatingClass
            {
                Rating = ct.Certification.Rating,
                Iso31661 = ct.Certification.Iso31661,
                Image = new(
                    $"/{ct.Certification.Iso31661}/{ct.Certification.Iso31661}_{ct.Certification.Rating}.svg"
                ),
            })
            .FirstOrDefault();
    }

    public CardData(Collection collection, string country, bool watch = false)
    {
        string? title = collection.Translations.FirstOrDefault()?.Title;
        string? overview = collection.Translations.FirstOrDefault()?.Overview;

        Id = collection.Id;
        Title = !string.IsNullOrEmpty(title) ? title : collection.Title;
        Overview = !string.IsNullOrEmpty(overview) ? overview : collection.Overview;
        Poster = collection.Poster;
        Backdrop = collection.Backdrop;
        Logo = collection.Images.FirstOrDefault(i => i.Type == "logo")?.FilePath;
        TitleSort = collection.Title.TitleSort(
            collection.CollectionMovies.MinBy(m => m.Movie.ReleaseDate)?.Movie.ReleaseDate
        );
        Year = collection
            .CollectionMovies.MinBy(m => m.Movie.ReleaseDate)
            ?.Movie.ReleaseDate.ParseYear();
        Type = MediaTypes.CollectionMediaType;

        Link = watch
            ? new($"/collection/{Id}/watch", UriKind.Relative)
            : new($"/collection/{Id}", UriKind.Relative);
        NumberOfItems = collection.CollectionMovies.Count;
        HaveItems = collection.CollectionMovies.Count(m =>
            m.Movie.VideoFiles.Any(v => v.Folder != null)
        );
        ColorPalette = collection.ColorPalette;
        CreatedAt = collection.CreatedAt;

        Rating = collection
            .CollectionMovies.SelectMany(cm => cm.Movie.CertificationMovies)
            .Where(cm => cm.Certification.Iso31661 == "US" || cm.Certification.Iso31661 == country)
            .Select(cm => new RatingClass
            {
                Rating = cm.Certification.Rating,
                Iso31661 = cm.Certification.Iso31661,
                Image = new(
                    $"/{cm.Certification.Iso31661}/{cm.Certification.Iso31661}_{cm.Certification.Rating}.svg"
                ),
            })
            .FirstOrDefault();
    }

    public CardData(Special special, string country, bool watch = false)
    {
        Id = special.Id;
        Title = special.Title.OrEmpty();
        Overview = special.Overview;
        Poster = special.Poster;
        Backdrop = special.Backdrop;
        Logo = special.Logo;
        TitleSort = special.Title.TitleSort();
        Year =
            special.Items.MinBy(m => m.Movie?.ReleaseDate)?.Movie?.ReleaseDate.ParseYear()
            ?? special.Items.Select(t => t.Episode?.Tv).FirstOrDefault()?.FirstAirDate.ParseYear();
        Type = MediaTypes.SpecialMediaType;
        Link = watch
            ? new($"/specials/{Id}/watch", UriKind.Relative)
            : new($"/specials/{Id}", UriKind.Relative);
        NumberOfItems = special.Items.Count;
        CreatedAt = special.CreatedAt;

        int haveMovies = special
            .Items.Select(i => i.Movie)
            .Count(m => m is not null && m.VideoFiles.Count != 0);
        int haveEpisodes = special
            .Items.Select(i => i.Episode)
            .Count(e => e is not null && e.VideoFiles.Count != 0);
        HaveItems = haveMovies + haveEpisodes;
        ColorPalette = special.ColorPalette;

        Rating = special
            .Items.SelectMany(i => i.Movie?.CertificationMovies ?? [])
            .Where(cm => cm.Certification.Iso31661 == "US" || cm.Certification.Iso31661 == country)
            .Select(cm => new RatingClass
            {
                Rating = cm.Certification.Rating,
                Iso31661 = cm.Certification.Iso31661,
                Image = new(
                    $"/{cm.Certification.Iso31661}/{cm.Certification.Iso31661}_{cm.Certification.Rating}.svg"
                ),
            })
            .FirstOrDefault();
    }

    public CardData(UserData item, string country)
    {
        Id = (
            item.SpecialId?.ToString()
            ?? item.CollectionId?.ToString()
            ?? item.MovieId?.ToString()
            ?? item.TvId?.ToString()
        ).OrEmpty();

        if (item.Special is not null)
        {
            ColorPalette = item.Special.ColorPalette;
            Poster = item.Special.Poster;
            Backdrop = item.Special.Backdrop;
            Title = item.Special.Title.OrEmpty();
            TitleSort = item.Special.Title.TitleSort();
            Overview = item.Special.Overview;
            Logo = item.Special.Logo;
            Duration = item.VideoFile.Duration?.ToSeconds();
            Type = MediaTypes.SpecialMediaType;
            Link = new($"/specials/{Id}/watch", UriKind.Relative);
            NumberOfItems = item.Special.Items.Count;
            CreatedAt = item.Special.CreatedAt;

            int availableMovies = item.Special.Items.Count(specialItem =>
                specialItem is { MovieId: not null, Movie.VideoFiles.Count: > 0 }
            );
            int availableEpisodes = item.Special.Items.Count(specialItem =>
                specialItem.Episode is { VideoFiles.Count: > 0 }
            );
            HaveItems = availableMovies + availableEpisodes;

            Rating = item
                .Special.Items.SelectMany(specialItem =>
                    specialItem
                        .Episode?.Tv.CertificationTvs.Where(ct =>
                            ct.Certification.Iso31661 == "US"
                            || ct.Certification.Iso31661 == country
                        )
                        .Select(ct => new RatingClass
                        {
                            Rating = ct.Certification.Rating,
                            Iso31661 = ct.Certification.Iso31661,
                            Image = new(
                                $"/{ct.Certification.Iso31661}/{ct.Certification.Iso31661}_{ct.Certification.Rating}.svg"
                            ),
                        })
                    ?? []
                )
                .Concat(
                    item.Special.Items.Where(specialItem => specialItem.MovieId != null)
                        .SelectMany(specialItem =>
                            specialItem
                                .Movie?.CertificationMovies.Where(cm =>
                                    cm.Certification.Iso31661 == "US"
                                    || cm.Certification.Iso31661 == country
                                )
                                .Select(cm => new RatingClass
                                {
                                    Rating = cm.Certification.Rating,
                                    Iso31661 = cm.Certification.Iso31661,
                                    Image = new(
                                        $"/{cm.Certification.Iso31661}/{cm.Certification.Iso31661}_{cm.Certification.Rating}.svg"
                                    ),
                                })
                            ?? []
                        )
                )
                .OrderByDescending(cert => cert.Order)
                .FirstOrDefault();
        }
        else if (item.Collection is not null)
        {
            ColorPalette = item.Collection.ColorPalette;
            Poster = item.Collection.Poster;
            Backdrop = item.Collection.Backdrop;
            Title = item.Collection.Title;
            TitleSort = item.Collection.Title.TitleSort();
            Overview = item.Collection.Overview;
            Logo = item.Collection.Images.FirstOrDefault(i => i.Type == "logo")?.FilePath;
            Duration = item.VideoFile.Duration?.ToSeconds();
            Year =
                item.Collection.CollectionMovies.MinBy(movie =>
                        movie.Movie.ReleaseDate?.ParseYear()
                    )
                    ?.Movie.ReleaseDate.ParseYear()
                ?? 0;
            Type = MediaTypes.CollectionMediaType;
            Link = new($"/collection/{Id}/watch", UriKind.Relative);
            CreatedAt = item.Collection.CreatedAt;
            NumberOfItems = item.Collection.CollectionMovies.Count;
            HaveItems = item
                .Collection.CollectionMovies.SelectMany(cm => cm.Movie.VideoFiles)
                .Count(vf => vf.Folder != null);

            Rating = item
                .Collection.CollectionMovies.SelectMany(cm => cm.Movie.CertificationMovies)
                .Where(cm =>
                    cm.Certification.Iso31661 == "US" || cm.Certification.Iso31661 == country
                )
                .Select(cm => new RatingClass
                {
                    Rating = cm.Certification.Rating,
                    Iso31661 = cm.Certification.Iso31661,
                    Image = new(
                        $"/{cm.Certification.Iso31661}/{cm.Certification.Iso31661}_{cm.Certification.Rating}.svg"
                    ),
                })
                .FirstOrDefault();
        }
        else if (item.Movie is not null)
        {
            ColorPalette = item.Movie.ColorPalette;
            Year = item.Movie.ReleaseDate.ParseYear();
            Poster = item.Movie.Poster;
            Backdrop = item.Movie.Backdrop;
            Title = item.Movie.Title;
            TitleSort = item.Movie.Title.TitleSort(item.Movie.ReleaseDate);
            Overview = item.Movie.Overview;
            Logo = item.Movie.Images.FirstOrDefault(i => i.Type == "logo")?.FilePath;
            Duration = item.VideoFile.Duration?.ToSeconds();
            Link = new($"/movie/{Id}/watch", UriKind.Relative);
            Type = MediaTypes.MovieMediaType;
            CreatedAt = item.Movie.CreatedAt;
            NumberOfItems = 1;
            HaveItems = item.Movie.VideoFiles.Count(v => v.Folder != null);

            Rating = item
                .Movie.CertificationMovies.Where(cm =>
                    cm.Certification.Iso31661 == "US" || cm.Certification.Iso31661 == country
                )
                .Select(cm => new RatingClass
                {
                    Rating = cm.Certification.Rating,
                    Iso31661 = cm.Certification.Iso31661,
                    Image = new(
                        $"/{cm.Certification.Iso31661}/{cm.Certification.Iso31661}_{cm.Certification.Rating}.svg"
                    ),
                })
                .FirstOrDefault();
        }
        else if (item.Tv is not null)
        {
            ColorPalette = item.Tv.ColorPalette;
            Year = item.Tv.FirstAirDate.ParseYear();
            Poster = item.Tv.Poster;
            Backdrop = item.Tv.Backdrop;
            Title = item.Tv.Title;
            TitleSort = item.Tv.Title.TitleSort(item.Tv.FirstAirDate);
            Overview = item.Tv.Overview;
            Logo = item.Tv.Images.FirstOrDefault(i => i.Type == "logo")?.FilePath;
            Duration = item.VideoFile.Duration?.ToSeconds();
            Link = new($"/tv/{Id}/watch", UriKind.Relative);
            Type = "tv";
            CreatedAt = item.Tv.CreatedAt;
            NumberOfItems = item.Tv.NumberOfEpisodes;
            HaveItems = item.Tv.Episodes.Count(episode =>
                episode.VideoFiles.Any(v => v.Folder != null)
            );

            Rating = item
                .Tv.CertificationTvs.Where(ct =>
                    ct.Certification.Iso31661 == "US" || ct.Certification.Iso31661 == country
                )
                .Select(ct => new RatingClass
                {
                    Rating = ct.Certification.Rating,
                    Iso31661 = ct.Certification.Iso31661,
                    Image = new(
                        $"/{ct.Certification.Iso31661}/{ct.Certification.Iso31661}_{ct.Certification.Rating}.svg"
                    ),
                })
                .FirstOrDefault();
        }
    }

    public CardData(Genre genre)
    {
        Id = genre.Id;
        Title = genre.Name;
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

    public CardData(NmCardDto dto)
    {
        Id = dto.Id;
        Title = dto.Title;
        TitleSort = dto.TitleSort.OrEmpty();
        Overview = dto.Overview;
        Link = dto.Link;
        Rating = dto.Rating;
        Year = dto.Year;
        Duration = dto.Duration;
        Type = dto.Type.OrEmpty();
        Backdrop = dto.Backdrop;
        Poster = dto.Poster;
        Logo = dto.Logo;
        ColorPalette = dto.ColorPalette;
        HaveItems = dto.HaveItems;
        NumberOfItems = dto.NumberOfItems;
    }

    public CardData(CollectionListDto dto, bool watch = false)
    {
        Id = dto.Id;
        Title = !string.IsNullOrEmpty(dto.TranslatedTitle) ? dto.TranslatedTitle : dto.Title;
        Overview = !string.IsNullOrEmpty(dto.TranslatedOverview)
            ? dto.TranslatedOverview
            : dto.Overview;
        Poster = dto.Poster;
        Backdrop = dto.Backdrop;
        Logo = dto.Logo;
        TitleSort = dto.TitleSort;
        ColorPalette = dto.ColorPalette;
        Year = dto.FirstMovieYear;
        Type = MediaTypes.CollectionMediaType;
        Link = watch
            ? new($"/collection/{dto.Id}/watch", UriKind.Relative)
            : new($"/collection/{dto.Id}", UriKind.Relative);
        NumberOfItems = dto.TotalMovies;
        HaveItems = dto.MoviesWithVideo;
        CreatedAt = dto.CreatedAt;

        if (
            !string.IsNullOrEmpty(dto.CertificationRating)
            && !string.IsNullOrEmpty(dto.CertificationCountry)
        )
        {
            Rating = new()
            {
                Rating = dto.CertificationRating,
                Iso31661 = dto.CertificationCountry,
                Image = new(
                    $"/{dto.CertificationCountry}/{dto.CertificationCountry}_{dto.CertificationRating}.svg"
                ),
            };
        }
    }

    public CardData(MovieCardDto movie, bool watch = false)
    {
        Id = movie.Id;
        Title = movie.Title;
        TitleSort = movie.TitleSort;
        Overview = movie.Overview;
        Poster = movie.Poster;
        Backdrop = movie.Backdrop;
        Logo = movie.Logo;
        Year = movie.ReleaseDate.ParseYear();
        Type = MediaTypes.MovieMediaType;
        CreatedAt = movie.CreatedAt;

        Link = watch
            ? new($"/movie/{Id}/watch", UriKind.Relative)
            : new($"/movie/{Id}", UriKind.Relative);
        NumberOfItems = 1;
        HaveItems = movie.VideoFileCount;

        ColorPalette = ColorPalette.FromJsonOrNull(movie.ColorPalette);

        if (movie.CertificationRating != null)
        {
            Rating = new()
            {
                Rating = movie.CertificationRating,
                Iso31661 = movie.CertificationCountry!,
                Image = new(
                    $"/{movie.CertificationCountry}/{movie.CertificationCountry}_{movie.CertificationRating}.svg"
                ),
            };
        }
    }

    public CardData(HomeMovieCardDto movie, bool watch = false)
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
        Type = MediaTypes.MovieMediaType;
        CreatedAt = movie.CreatedAt;
        Link = watch
            ? new($"/movie/{Id}/watch", UriKind.Relative)
            : new($"/movie/{Id}", UriKind.Relative);
        NumberOfItems = 1;
        HaveItems = movie.VideoFileCount;

        ColorPalette = ColorPalette.FromJsonOrNull(movie.ColorPalette);

        if (movie.CertificationRating != null)
        {
            Rating = new()
            {
                Rating = movie.CertificationRating,
                Iso31661 = movie.CertificationCountry!,
                Image = new(
                    $"/{movie.CertificationCountry}/{movie.CertificationCountry}_{movie.CertificationRating}.svg"
                ),
            };
        }
    }

    public CardData(HomeTvCardDto tv, bool watch = false)
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
        Type = "tv";
        CreatedAt = tv.CreatedAt;
        Link = watch
            ? new($"/tv/{Id}/watch", UriKind.Relative)
            : new($"/tv/{Id}", UriKind.Relative);
        NumberOfItems = tv.NumberOfEpisodes;
        HaveItems = tv.EpisodesWithVideo;

        ColorPalette = ColorPalette.FromJsonOrNull(tv.ColorPalette);

        if (tv.CertificationRating != null)
        {
            Rating = new()
            {
                Rating = tv.CertificationRating,
                Iso31661 = tv.CertificationCountry!,
                Image = new(
                    $"/{tv.CertificationCountry}/{tv.CertificationCountry}_{tv.CertificationRating}.svg"
                ),
            };
        }
    }

    public CardData(SpecialCardDto dto)
    {
        Id = dto.Id;
        Title = dto.Title;
        Overview = dto.Overview;
        Poster = dto.Poster;
        Backdrop = dto.Backdrop;
        Logo = dto.Logo;
        TitleSort = dto.TitleSort;
        Type = MediaTypes.SpecialMediaType;
        Link = new($"/specials/{dto.Id}", UriKind.Relative);
        NumberOfItems = dto.NumberOfItems;
        CreatedAt = dto.CreatedAt;
        HaveItems = dto.HaveMovies + dto.HaveEpisodes;

        ColorPalette = ColorPalette.FromJsonOrNull(dto.ColorPalette);

        if (dto.CertificationRating != null)
        {
            Rating = new()
            {
                Rating = dto.CertificationRating,
                Iso31661 = dto.CertificationCountry!,
                Image = new(
                    $"/{dto.CertificationCountry}/{dto.CertificationCountry}_{dto.CertificationRating}.svg"
                ),
            };
        }
    }

    public CardData(RecommendationDto rec)
    {
        Id = rec.Id;
        Title = rec.Title.OrEmpty();
        TitleSort = rec.TitleSort.OrEmpty();
        Overview = rec.Overview;
        Poster = rec.Poster;
        Backdrop = rec.Backdrop;
        Type = rec.Type;
        Link = rec.Link;
        NumberOfItems = 0;
        HaveItems = 0;
        ColorPalette = rec.ColorPalette;
    }

    public CardData(TvCardDto tv, bool watch = false)
    {
        Id = tv.Id;
        Title = tv.Title;
        TitleSort = tv.TitleSort;
        Overview = tv.Overview;
        Poster = tv.Poster;
        Backdrop = tv.Backdrop;
        Logo = tv.Logo;
        Year = tv.FirstAirDate.ParseYear();
        Type = "tv";
        CreatedAt = tv.CreatedAt;

        Link = watch
            ? new($"/tv/{Id}/watch", UriKind.Relative)
            : new($"/tv/{Id}", UriKind.Relative);
        NumberOfItems = tv.NumberOfEpisodes;
        HaveItems = tv.EpisodesWithVideo;

        ColorPalette = ColorPalette.FromJsonOrNull(tv.ColorPalette);

        if (tv.CertificationRating != null)
        {
            Rating = new()
            {
                Rating = tv.CertificationRating,
                Iso31661 = tv.CertificationCountry!,
                Image = new(
                    $"/{tv.CertificationCountry}/{tv.CertificationCountry}_{tv.CertificationRating}.svg"
                ),
            };
        }
    }
}
