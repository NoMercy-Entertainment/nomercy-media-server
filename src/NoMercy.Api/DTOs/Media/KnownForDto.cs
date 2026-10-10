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
using NoMercy.Database.Models.People;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.TMDB.Models.People;

namespace NoMercy.Api.DTOs.Media;

public record KnownForDto
{
    [JsonProperty("adult")]
    public bool Adult { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("genre_ids")]
    public int[]? GenreIds { get; set; }

    [JsonProperty("id")]
    public int? Id { get; set; }

    [JsonProperty("original_language")]
    public string OriginalLanguage { get; set; } = string.Empty;

    [JsonProperty("original_title")]
    public string OriginalTitle { get; set; } = string.Empty;

    [JsonProperty("overview")]
    public string Overview { get; set; } = string.Empty;

    [JsonProperty("popularity")]
    public double Popularity { get; set; }

    [JsonProperty("release_date")]
    public DateTime? ReleaseDate { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("video")]
    public bool? Video { get; set; }

    [JsonProperty("vote_average")]
    public double VoteAverage { get; set; }

    [JsonProperty("vote_count")]
    public long VoteCount { get; set; }

    [JsonProperty("character")]
    public string? Character { get; set; }

    [JsonProperty("credit_id")]
    public string CreditId { get; set; } = string.Empty;

    [JsonProperty("order")]
    public long? Order { get; set; }

    [JsonProperty("media_type")]
    public string? MediaType { get; set; }

    [JsonProperty("hasItem")]
    public bool? HasItem { get; set; }

    [JsonProperty("poster")]
    public string Poster { get; set; } = string.Empty;

    [JsonProperty("year")]
    public long? Year { get; set; }

    [JsonProperty("origin_country")]
    public string[] OriginCountry { get; set; } = [];

    [JsonProperty("original_name")]
    public string OriginalName { get; set; } = string.Empty;

    [JsonProperty("first_air_date")]
    public DateTimeOffset? FirstAirDate { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("department")]
    public string? Department { get; set; }

    [JsonProperty("job")]
    public string? Job { get; set; }

    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("episode_count")]
    public int? EpisodeCount { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    public KnownForDto(Cast cast)
    {
        Character = cast.Role.Character;
        Title = cast.Movie?.Title ?? cast.Tv?.Title;
        MediaType = cast.Movie is not null ? MediaTypes.MovieMediaType : MediaTypes.TvMediaType;
        Year = cast.Movie?.ReleaseDate.ParseYear() ?? cast.Tv?.FirstAirDate.ParseYear();
        Id = cast.Movie?.Id ?? cast.Tv?.Id;
        Adult = cast.Movie?.Adult ?? false;
        OriginalLanguage = (cast.Movie?.OriginalLanguage ?? cast.Tv?.OriginalLanguage).OrEmpty();
        Overview = (cast.Movie?.Overview ?? cast.Tv?.Overview).OrEmpty();
        Popularity = cast.Movie?.Popularity ?? cast.Tv?.Popularity ?? 0;
        Poster = (cast.Movie?.Poster ?? cast.Tv?.Poster).OrEmpty();
        Backdrop = cast.Movie?.Backdrop ?? cast.Tv?.Backdrop;
        ReleaseDate = cast.Movie?.ReleaseDate ?? cast.Tv?.FirstAirDate;
        VoteAverage = cast.Movie?.VoteAverage ?? cast.Tv?.VoteAverage ?? 0;
        VoteCount = cast.Movie?.VoteCount ?? cast.Tv?.VoteCount ?? 0;
        Link = new($"/{MediaType}/{Id}", UriKind.Relative);
        HasItem =
            cast.Movie?.VideoFiles.Count > 0
            || (cast.Tv?.Episodes.Any(e => e.VideoFiles.Count != 0) ?? false);
        NumberOfItems =
            (cast.Movie?.VideoFiles.Count ?? 0)
            + (cast.Tv?.Episodes.Count(e => e.VideoFiles.Count != 0) ?? 0);
        HaveItems =
            cast.Movie?.VideoFiles.Count > 0
                ? 1
                : cast.Tv?.Episodes.Count(e => e.VideoFiles.Count != 0) ?? 0;
        ColorPalette = cast.Movie?.ColorPalette ?? cast.Tv?.ColorPalette;
    }

    public KnownForDto(Crew crew)
    {
        Title = crew.Movie?.Title ?? crew.Tv!.Title;
        MediaType = crew.Movie is not null ? MediaTypes.MovieMediaType : MediaTypes.TvMediaType;
        Year = crew.Movie?.ReleaseDate.ParseYear() ?? crew.Tv!.FirstAirDate.ParseYear();
        Id = crew.Movie?.Id ?? crew.Tv!.Id;
        Adult = crew.Movie?.Adult ?? false;
        Backdrop = crew.Movie?.Backdrop ?? crew.Tv!.Backdrop;
        OriginalLanguage = (crew.Movie?.OriginalLanguage ?? crew.Tv!.OriginalLanguage).OrEmpty();
        Overview = (crew.Movie?.Overview ?? crew.Tv!.Overview).OrEmpty();
        Popularity = crew.Movie?.Popularity ?? crew.Tv!.Popularity ?? 0;
        Poster = (crew.Movie?.Poster ?? crew.Tv!.Poster).OrEmpty();
        ReleaseDate = crew.Movie?.ReleaseDate ?? crew.Tv!.FirstAirDate;
        VoteAverage = crew.Movie?.VoteAverage ?? crew.Tv!.VoteAverage ?? 0;
        VoteCount = crew.Movie?.VoteCount ?? crew.Tv!.VoteCount ?? 0;
        Job = crew.Job.Task.OrEmpty();
        Link = new($"/{MediaType}/{Id}", UriKind.Relative);
        HasItem =
            crew.Movie?.VideoFiles.Count > 0
            || (crew.Tv?.Episodes.Any(e => e.VideoFiles.Count != 0) ?? false);
        NumberOfItems =
            (crew.Movie?.VideoFiles.Count ?? 0)
            + (crew.Tv?.Episodes.Count(e => e.VideoFiles.Count > 0) ?? 0);
        HaveItems =
            crew.Movie?.VideoFiles.Count > 0
                ? 1
                : crew.Tv?.Episodes.Count(e => e.VideoFiles.Count > 0) ?? 0;
        ColorPalette = crew.Movie?.ColorPalette ?? crew.Tv?.ColorPalette;
    }

    public KnownForDto(TmdbPersonCredit crew, Person? person)
    {
        int year = crew.ReleaseDate.ParseYear();
        if (year == 0)
            year = crew.FirstAirDate.ParseYear();

        Character = crew.Character;
        Title = crew.Title ?? crew.Name;
        Backdrop = crew.BackdropPath;
        MediaType = crew.MediaType;
        Type = crew.MediaType;
        Id = crew.Id;
        HasItem = false;
        Adult = crew.Adult;
        Popularity = crew.Popularity;
        Character = crew.Character;
        Job = crew.Job;
        Department = crew.Department;
        Year = year;
        OriginalLanguage = crew.OriginalLanguage;
        Overview = crew.Overview;
        Popularity = crew.Popularity;
        Poster = crew.PosterPath;
        VoteAverage = crew.VoteAverage;
        VoteCount = crew.VoteCount;
        Job = crew.Job;
        EpisodeCount = crew.EpisodeCount;
        Link = new($"/{crew.MediaType}/{Id}", UriKind.Relative);

        NumberOfItems =
            person
                ?.Casts.Where(c =>
                    c.MovieId == crew.Id
                    || c.TvId == crew.Id
                    || c.SeasonId == crew.Id
                    || c.EpisodeId == crew.Id
                )
                .Sum(c =>
                    (c.Movie != null && c.Movie.VideoFiles.Count != 0 ? 1 : 0)
                    + (c.Tv?.NumberOfEpisodes ?? 0)
                )
            ?? 0;

        HasItem =
            person?.Casts.Any(c =>
                (
                    c.MovieId == crew.Id
                    || c.TvId == crew.Id
                    || c.SeasonId == crew.Id
                    || c.EpisodeId == crew.Id
                )
                && (
                    c.Movie?.VideoFiles.Count > 0
                    || c.Tv?.Episodes.Any(e => e.VideoFiles.Count != 0) != null
                )
            ) == true;

        HaveItems =
            person
                ?.Casts.Where(c =>
                    c.MovieId == crew.Id
                    || c.TvId == crew.Id
                    || c.SeasonId == crew.Id
                    || c.EpisodeId == crew.Id
                )
                .Sum(c =>
                    (c.Movie is { VideoFiles.Count: > 0 } ? 1 : 0)
                    + (c.Tv != null ? c.Tv.Episodes.Count(e => e.VideoFiles.Count != 0) : 0)
                )
            ?? 0;
    }

    public KnownForDto(TmdbPersonCredit crew, string type, Person? person)
    {
        int year = crew.ReleaseDate.ParseYear();
        if (year == 0)
            year = crew.FirstAirDate.ParseYear();
        Character = crew.Character;
        Title = crew.Title ?? crew.Name;
        Backdrop = crew.BackdropPath;
        MediaType = type;
        Type = type;
        Id = crew.Id;
        HasItem = false;
        Adult = crew.Adult;
        Popularity = crew.Popularity;
        Character = crew.Character;
        Job = crew.Job;
        Department = crew.Department;
        Year = year;
        OriginalLanguage = crew.OriginalLanguage;
        Overview = crew.Overview;
        Popularity = crew.Popularity;
        Poster = crew.PosterPath;
        VoteAverage = crew.VoteAverage;
        VoteCount = crew.VoteCount;
        Job = crew.Job;
        EpisodeCount = crew.EpisodeCount;
        Link = new($"/{crew.MediaType}/{Id}", UriKind.Relative);

        HasItem =
            person?.Crews.Any(c =>
                (
                    c.MovieId == crew.Id
                    || c.TvId == crew.Id
                    || c.SeasonId == crew.Id
                    || c.EpisodeId == crew.Id
                )
                && (
                    c.Movie?.VideoFiles.Count > 0
                    || c.Tv?.Episodes.Any(e => e.VideoFiles.Count != 0) != null
                )
            ) == true;

        NumberOfItems =
            person
                ?.Crews.Where(c =>
                    c.MovieId == crew.Id
                    || c.TvId == crew.Id
                    || c.SeasonId == crew.Id
                    || c.EpisodeId == crew.Id
                )
                .Sum(c =>
                    (c.Movie != null && c.Movie.VideoFiles.Count != 0 ? 1 : 0)
                    + (c.Tv?.NumberOfEpisodes ?? 0)
                )
            ?? 0;

        HaveItems =
            person
                ?.Crews.Where(c =>
                    c.MovieId == crew.Id
                    || c.TvId == crew.Id
                    || c.SeasonId == crew.Id
                    || c.EpisodeId == crew.Id
                )
                .Sum(c =>
                    (c.Movie is { VideoFiles.Count: > 0 } ? 1 : 0)
                    + (c.Tv != null ? c.Tv.Episodes.Count(e => e.VideoFiles.Count != 0) : 0)
                )
            ?? 0;
    }
}
