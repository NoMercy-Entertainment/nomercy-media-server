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
using NoMercy.Data.DTOs.Specials;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public record SpecialResponseItemDto
{
    [JsonProperty("id")]
    public Ulid Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("titleSort")]
    public string TitleSort { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("collection")]
    public IEnumerable<SpecialItemDto>? Special { get; set; }

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("watched")]
    public bool Watched { get; set; }

    [JsonProperty("genres")]
    public IEnumerable<GenreDto> Genres { get; set; }

    [JsonProperty("total_duration")]
    public int TotalDuration { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("cast")]
    public IEnumerable<PeopleDto> Cast { get; set; }

    [JsonProperty("crew")]
    public IEnumerable<PeopleDto> Crew { get; set; }

    [JsonProperty("backdrops")]
    public IEnumerable<ImageDto> Backdrops { get; set; }

    [JsonProperty("posters")]
    public IEnumerable<ImageDto> Posters { get; set; }

    [JsonProperty("content_ratings")]
    public IEnumerable<Certification?> ContentRatings { get; set; }

    [JsonProperty("vote_average")]
    public double VoteAverage { get; set; }

    public SpecialResponseItemDto(Special special, List<SpecialItemsDto> items)
    {
        List<SpecialItemDto> specialItems = [];
        foreach (SpecialItem specialItem in special.Items)
            if (specialItem.MovieId is not null)
            {
                SpecialItemsDto? newItem = items.Find(i =>
                    i.MediaType == MediaTypes.MovieMediaType && i.Id == specialItem.MovieId
                );
                if (newItem is null)
                    continue;

                SpecialItemDto item = new(newItem);
                specialItems.Add(item);
            }
            else
            {
                SpecialItemsDto? newItem = items.FirstOrDefault(i =>
                    i.EpisodeIds.Contains(specialItem.EpisodeId ?? 0)
                );
                if (newItem is null)
                    continue;

                SpecialItemDto item = new(newItem);
                specialItems.Add(item);
            }

        IEnumerable<PeopleDto> cast =
        [
            .. items.SelectMany(tv => tv.Cast).DistinctBy(people => people.Id),
        ];

        IEnumerable<PeopleDto> crew =
        [
            .. items.SelectMany(item => item.Crew).DistinctBy(people => people.Id),
        ];

        IEnumerable<ImageDto> posters = [.. items.SelectMany(item => item.Posters)];

        IEnumerable<ImageDto> backdrops = [.. items.SelectMany(item => item.Backdrops)];

        IEnumerable<GenreDto> genres =
        [
            .. items.SelectMany(item => item.Genres).DistinctBy(genre => genre.Id),
        ];

        foreach (SpecialItemsDto item in items)
        {
            item.Posters = [];
            item.Backdrops = [];
            item.Cast = [];
            item.Crew = [];
            item.Genres = [];
        }

        Id = special.Id;
        Title = special.Title.OrEmpty();
        Overview = special.Overview;
        Backdrop = special.Backdrop?.Replace("https://storage.nomercy.tv/laravel", "");
        Poster = special.Poster;
        Logo = special.Logo;
        TitleSort = special.Title.TitleSort();
        Type = "specials";
        MediaType = "specials";
        Link = new($"/specials/{Id}", UriKind.Relative);
        ColorPalette = special.ColorPalette;
        Backdrops = backdrops;
        Posters = posters;
        Cast = cast;
        Crew = crew;
        Genres = genres;

        Favorite = special.SpecialUser.Count != 0;
        Watched =
            special.Items.Count(specialItem => specialItem.UserData.Count > 0)
            == special.Items.Count;

        NumberOfItems = special.Items.Count;

        int haveMovies = special.Items.Count(item =>
            item.MovieId is not null && item.Movie?.VideoFiles.Count > 0
        );
        int haveEpisodes = special.Items.Count(item =>
            item.EpisodeId is not null && item.Episode?.VideoFiles.Count > 0
        );
        HaveItems = haveMovies + haveEpisodes;

        TotalDuration = items.Sum(item => item.TotalDuration);

        VoteAverage =
            items.Where(item => item.VoteAverage != null).Select(item => item.VoteAverage).Average()
            ?? 0;

        ContentRatings = items
            .Select(specialItem => specialItem.Rating)
            .DistinctBy(rating => rating.Iso31661);

        Special = specialItems.DistinctBy(si => (si.MediaType, si.Id));
    }

    public SpecialResponseItemDto(Special special)
    {
        Id = special.Id;
        Title = special.Title.OrEmpty();
        Overview = special.Overview;
        Backdrop = special.Backdrop?.Replace("https://storage.nomercy.tv/laravel", "");
        Logo = special.Logo;
        Poster = special.Poster;
        TitleSort = special.Title.TitleSort();
        Type = "specials";
        MediaType = "specials";
        Link = new($"/specials/{Id}", UriKind.Relative);
        ColorPalette = special.ColorPalette;
        Favorite = special.SpecialUser.Count != 0;
        NumberOfItems = special.Items.Count;

        int movies = special.Items.Count(item =>
            item.MovieId is not null && item.Movie?.VideoFiles.Count > 0
        );
        int episodes = special.Items.Count(item =>
            item.EpisodeId is not null && item.Episode?.VideoFiles.Count > 0
        );

        HaveItems = movies + episodes;

        Cast = [];
        Crew = [];
        Backdrops = [];
        Posters = [];
        Genres = [];

        TotalDuration = special.Items.Sum(item => item.Movie?.Runtime ?? 0);

        VoteAverage =
            special
                .Items.Where(item => item.Movie?.VoteAverage != null)
                .Select(item => item.Movie?.VoteAverage)
                .Average()
            ?? 0;

        ContentRatings = special
            .Items.Select(specialItem =>
                specialItem
                    .Movie?.CertificationMovies.Select(certification => certification.Certification)
                    .FirstOrDefault()
            )
            .DistinctBy(rating => rating?.Iso31661);
    }

    public SpecialResponseItemDto(SpecialDetailDto detail, List<SpecialItemsDto> items)
    {
        List<SpecialItemDto> specialItems = [];
        foreach (SpecialItemRefDto itemRef in detail.Items)
            if (itemRef.MovieId is not null)
            {
                SpecialItemsDto? newItem = items.Find(i =>
                    i.MediaType == MediaTypes.MovieMediaType && i.Id == itemRef.MovieId
                );
                if (newItem is null)
                    continue;

                SpecialItemDto item = new(newItem);
                specialItems.Add(item);
            }
            else
            {
                SpecialItemsDto? newItem = items.FirstOrDefault(i =>
                    i.EpisodeIds.Contains(itemRef.EpisodeId ?? 0)
                );
                if (newItem is null)
                    continue;

                SpecialItemDto item = new(newItem);
                specialItems.Add(item);
            }

        IEnumerable<PeopleDto> cast =
        [
            .. items.SelectMany(tv => tv.Cast).DistinctBy(people => people.Id),
        ];

        IEnumerable<PeopleDto> crew =
        [
            .. items.SelectMany(item => item.Crew).DistinctBy(people => people.Id),
        ];

        IEnumerable<ImageDto> posters = [.. items.SelectMany(item => item.Posters)];

        IEnumerable<ImageDto> backdrops = [.. items.SelectMany(item => item.Backdrops)];

        IEnumerable<GenreDto> genres =
        [
            .. items.SelectMany(item => item.Genres).DistinctBy(genre => genre.Id),
        ];

        foreach (SpecialItemsDto item in items)
        {
            item.Posters = [];
            item.Backdrops = [];
            item.Cast = [];
            item.Crew = [];
            item.Genres = [];
        }

        Id = detail.Id;
        Title = detail.Title;
        Overview = detail.Overview;
        Backdrop = detail.Backdrop?.Replace("https://storage.nomercy.tv/laravel", "");
        Poster = detail.Poster;
        Logo = detail.Logo;
        TitleSort = detail.Title.TitleSort();
        Type = "specials";
        MediaType = "specials";
        Link = new($"/specials/{Id}", UriKind.Relative);
        ColorPalette = ColorPalette.FromJsonOrNull(detail.ColorPalette);
        Backdrops = backdrops;
        Posters = posters;
        Cast = cast;
        Crew = crew;
        Genres = genres;

        Favorite = detail.Favorite;

        NumberOfItems = detail.NumberOfItems;
        HaveItems = detail.HaveMovies + detail.HaveEpisodes;

        TotalDuration = items.Sum(item => item.TotalDuration);

        VoteAverage =
            items.Where(item => item.VoteAverage != null).Select(item => item.VoteAverage).Average()
            ?? 0;

        ContentRatings = items
            .Select(specialItem => specialItem.Rating)
            .DistinctBy(rating => rating.Iso31661);

        Special = specialItems.DistinctBy(si => (si.MediaType, si.Id));
    }
}
