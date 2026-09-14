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
        : this(new NmCardDto(movie, country), watch) { }

    public CardData(Tv tv, string country, bool watch = false)
        : this(new NmCardDto(tv, country), watch) { }

    public CardData(Collection collection, string country, bool watch = false)
        : this(new NmCardDto(collection, country), watch) { }

    public CardData(Special special, string country, bool watch = false)
        : this(new NmCardDto(special, country), watch) { }

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
                            RatingClass.IsShownIn(ct.Certification, country)
                        )
                        .Select(ct => RatingClass.From(ct.Certification))
                    ?? []
                )
                .Concat(
                    item.Special.Items.Where(specialItem => specialItem.MovieId != null)
                        .SelectMany(specialItem =>
                            specialItem
                                .Movie?.CertificationMovies.Where(cm =>
                                    RatingClass.IsShownIn(cm.Certification, country)
                                )
                                .Select(cm => RatingClass.From(cm.Certification))
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
                .Where(cm => RatingClass.IsShownIn(cm.Certification, country))
                .Select(cm => RatingClass.From(cm.Certification))
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
                    RatingClass.IsShownIn(cm.Certification, country)
                )
                .Select(cm => RatingClass.From(cm.Certification))
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
                .Tv.CertificationTvs.Where(ct => RatingClass.IsShownIn(ct.Certification, country))
                .Select(ct => RatingClass.From(ct.Certification))
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

    /// <summary>
    /// The card built from its component DTO, field for field; a watch card links
    /// to the title's player instead of its page.
    /// </summary>
    private CardData(NmCardDto card, bool watch)
    {
        Id = card.Id;
        Title = card.Title;
        TitleSort = card.TitleSort!;
        Overview = card.Overview;
        Link = watch ? new($"{card.Link.OriginalString}/watch", UriKind.Relative) : card.Link;
        Rating = card.Rating;
        Year = card.Year;
        Duration = card.Duration;
        Type = card.Type!;
        CreatedAt = card.CreatedAt;
        Backdrop = card.Backdrop;
        Poster = card.Poster;
        Logo = card.Logo;
        ColorPalette = card.ColorPalette;
        HaveItems = card.HaveItems;
        NumberOfItems = card.NumberOfItems;
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
        : this(new NmCardDto(dto), watch) { }

    public CardData(MovieCardDto movie, bool watch = false)
        : this(new NmCardDto(movie), watch) { }

    public CardData(HomeMovieCardDto movie, bool watch = false)
        : this(new NmCardDto(movie), watch) { }

    public CardData(HomeTvCardDto tv, bool watch = false)
        : this(new NmCardDto(tv), watch) { }

    public CardData(SpecialCardDto dto)
        : this(new NmCardDto(dto), false) { }

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
        : this(new NmCardDto(tv), watch) { }
}
