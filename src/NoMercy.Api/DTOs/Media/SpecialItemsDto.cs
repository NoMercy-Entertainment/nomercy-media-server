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
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public record SpecialItemsDto
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("episode_ids")]
    public int[] EpisodeIds { get; set; }

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

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("year")]
    public long Year { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("genres")]
    public IEnumerable<GenreDto> Genres { get; set; }

    [JsonProperty("backdrops")]
    public IEnumerable<ImageDto> Backdrops { get; set; }

    [JsonProperty("posters")]
    public IEnumerable<ImageDto> Posters { get; set; }

    [JsonProperty("cast")]
    public IEnumerable<PeopleDto> Cast { get; set; }

    [JsonProperty("crew")]
    public IEnumerable<PeopleDto> Crew { get; set; }

    [JsonProperty("rating")]
    public Certification Rating { get; set; }

    [JsonProperty("videoId")]
    public string? VideoId { get; set; }

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    [JsonProperty("have_items")]
    public int HaveItems { get; set; }

    [JsonProperty("duration")]
    public int Duration { get; set; }

    [JsonProperty("total_duration")]
    public int TotalDuration { get; set; }

    [JsonProperty("vote_average")]
    public double? VoteAverage { get; set; }

    public SpecialItemsDto(Movie movie)
    {
        Id = movie.Id;
        EpisodeIds = [];
        Title = movie.Title;
        Overview = movie.Overview;

        Backdrop = movie.Backdrop;
        Logo = movie.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;

        Backdrops = movie
            .Images.Where(media => media.Type == "backdrop")
            .Take(2)
            .Select(media => new ImageDto(media));

        Posters = movie
            .Images.Where(media => media.Type == "poster")
            .Take(2)
            .Select(media => new ImageDto(media));

        MediaType = MediaTypes.MovieMediaType;
        ColorPalette = movie.ColorPalette;
        Poster = movie.Poster;
        Type = MediaTypes.MovieMediaType;
        Link = new($"/movie/{Id}", UriKind.Relative);
        Year = movie.ReleaseDate.ParseYear();
        Duration = movie.Runtime * 60 ?? 0;

        TotalDuration = movie.Runtime * 60 ?? 0;

        Genres = movie.GenreMovies.Select(genreMovie => new GenreDto(genreMovie.Genre));

        Rating =
            movie
                .CertificationMovies.Select(certificationMovie => certificationMovie.Certification)
                .FirstOrDefault()
            ?? new Certification();

        VoteAverage = movie.VoteAverage;

        NumberOfItems = 1;
        HaveItems = movie.VideoFiles.Count > 0 ? 1 : 0;

        VideoId = movie.Video;

        Cast = movie.Cast.Take(15).Select(cast => new PeopleDto(cast));

        Crew = movie.Crew.Take(15).Select(crew => new PeopleDto(crew));
    }

    public SpecialItemsDto(Tv tv)
    {
        Id = tv.Id;
        EpisodeIds = tv.Episodes.Select(episode => episode.Id).ToArray();

        Title = tv.Title;
        Overview = tv.Overview;

        Backdrop = tv.Backdrop;
        Logo = tv.Images.FirstOrDefault(media => media.Type == "logo")?.FilePath;

        Backdrops = tv
            .Images.Where(media => media.Type == "backdrop")
            .Take(2)
            .Select(media => new ImageDto(media));

        Posters = tv
            .Images.Where(media => media.Type == "poster")
            .Take(2)
            .Select(media => new ImageDto(media));

        MediaType = "tv";
        ColorPalette = tv.ColorPalette;
        Poster = tv.Poster;
        Type = "tv";
        Link = new($"/tv/{Id}", UriKind.Relative);
        Year = tv.FirstAirDate.ParseYear();

        VoteAverage = tv.VoteAverage;

        Genres = tv.GenreTvs.Select(genreTv => new GenreDto(genreTv.Genre));

        Rating =
            tv.CertificationTvs.Select(certificationTv => certificationTv.Certification)
                .FirstOrDefault()
            ?? new Certification();

        NumberOfItems = tv.Episodes.Count(e => e.SeasonNumber > 0);
        int have = tv
            .Episodes.Where(e => e.SeasonNumber > 0)
            .Count(episode => episode.VideoFiles.Count != 0);

        HaveItems = have;

        Duration = tv.Duration * have * 60 ?? 0;

        TotalDuration = tv.Episodes.Sum(item =>
            item.VideoFiles.FirstOrDefault()?.Duration?.ToSeconds() ?? 0
        );

        VideoId = tv.Trailer;

        Cast = tv.Cast.Take(15).Select(cast => new PeopleDto(cast));

        Crew = tv.Crew.Take(15).Select(crew => new PeopleDto(crew));
    }

    public SpecialItemsDto(SpecialMovieProjection movie)
    {
        Id = movie.Id;
        EpisodeIds = [];
        Title = movie.Title;
        Overview = movie.Overview;
        Backdrop = movie.Backdrop;
        Logo = movie.Logo;

        Backdrops = movie.Backdrops.Select(i => new ImageDto
        {
            Id = i.Id,
            Src =
                i.Site == "https://image.tmdb.org/t/p/"
                    ? new Uri(i.FilePath!, UriKind.Relative).ToString()
                    : new Uri($"/images/music{i.FilePath}", UriKind.Relative).ToString(),
            Width = i.Width,
            Type = i.Type,
            Height = i.Height,
            Iso6391 = i.Iso6391,
            VoteAverage = i.VoteAverage,
            VoteCount = i.VoteCount,
            ColorPalette = ColorPalette.FromJsonOrNull(i.ColorPalette),
        });

        Posters = movie.Posters.Select(i => new ImageDto
        {
            Id = i.Id,
            Src =
                i.Site == "https://image.tmdb.org/t/p/"
                    ? new Uri(i.FilePath!, UriKind.Relative).ToString()
                    : new Uri($"/images/music{i.FilePath}", UriKind.Relative).ToString(),
            Width = i.Width,
            Type = i.Type,
            Height = i.Height,
            Iso6391 = i.Iso6391,
            VoteAverage = i.VoteAverage,
            VoteCount = i.VoteCount,
            ColorPalette = ColorPalette.FromJsonOrNull(i.ColorPalette),
        });

        MediaType = MediaTypes.MovieMediaType;
        ColorPalette = ColorPalette.FromJsonOrNull(movie.ColorPalette);
        Poster = movie.Poster;
        Type = MediaTypes.MovieMediaType;
        Link = new($"/movie/{movie.Id}", UriKind.Relative);
        Year = movie.ReleaseDate.ParseYear();
        Duration = movie.Runtime * 60 ?? 0;
        TotalDuration = movie.Runtime * 60 ?? 0;
        VoteAverage = movie.VoteAverage;

        Genres = movie.Genres.Select(g => new GenreDto
        {
            Id = g.Id,
            Name = g.Name,
            Link = new($"/genres/{g.Id}", UriKind.Relative),
        });

        Rating = new()
        {
            Rating = movie.CertificationRating.OrEmpty(),
            Iso31661 = movie.CertificationCountry.OrEmpty(),
        };

        NumberOfItems = 1;
        HaveItems = movie.VideoFileCount > 0 ? 1 : 0;
        VideoId = movie.Video;

        Cast = movie.Cast.Select(c => new PeopleDto
        {
            Id = c.PersonId,
            Name = c.PersonName,
            ProfilePath = c.PersonProfile,
            KnownForDepartment = c.PersonKnownForDepartment,
            ColorPalette = ColorPalette.FromJsonOrNull(c.PersonColorPalette),
            DeathDay = c.PersonDeathDay,
            Gender = c.PersonGender,
            Character = c.Character,
            Order = c.Order,
            Link = new($"/person/{c.PersonId}", UriKind.Relative),
            Translations = [],
        });

        Crew = movie.Crew.Select(c => new PeopleDto
        {
            Id = c.PersonId,
            Name = c.PersonName,
            ProfilePath = c.PersonProfile,
            KnownForDepartment = c.PersonKnownForDepartment,
            ColorPalette = ColorPalette.FromJsonOrNull(c.PersonColorPalette),
            DeathDay = c.PersonDeathDay,
            Gender = c.PersonGender,
            Job = c.Task,
            Order = c.Order,
            Link = new($"/person/{c.PersonId}", UriKind.Relative),
            Translations = [],
        });
    }

    public SpecialItemsDto(SpecialTvProjection tv)
    {
        Id = tv.Id;
        EpisodeIds = tv.EpisodeIds;
        Title = tv.Title;
        Overview = tv.Overview;
        Backdrop = tv.Backdrop;
        Logo = tv.Logo;

        Backdrops = tv.Backdrops.Select(i => new ImageDto
        {
            Id = i.Id,
            Src =
                i.Site == "https://image.tmdb.org/t/p/"
                    ? new Uri(i.FilePath!, UriKind.Relative).ToString()
                    : new Uri($"/images/music{i.FilePath}", UriKind.Relative).ToString(),
            Width = i.Width,
            Type = i.Type,
            Height = i.Height,
            Iso6391 = i.Iso6391,
            VoteAverage = i.VoteAverage,
            VoteCount = i.VoteCount,
            ColorPalette = ColorPalette.FromJsonOrNull(i.ColorPalette),
        });

        Posters = tv.Posters.Select(i => new ImageDto
        {
            Id = i.Id,
            Src =
                i.Site == "https://image.tmdb.org/t/p/"
                    ? new Uri(i.FilePath!, UriKind.Relative).ToString()
                    : new Uri($"/images/music{i.FilePath}", UriKind.Relative).ToString(),
            Width = i.Width,
            Type = i.Type,
            Height = i.Height,
            Iso6391 = i.Iso6391,
            VoteAverage = i.VoteAverage,
            VoteCount = i.VoteCount,
            ColorPalette = ColorPalette.FromJsonOrNull(i.ColorPalette),
        });

        MediaType = "tv";
        ColorPalette = ColorPalette.FromJsonOrNull(tv.ColorPalette);
        Poster = tv.Poster;
        Type = "tv";
        Link = new($"/tv/{tv.Id}", UriKind.Relative);
        Year = tv.FirstAirDate.ParseYear();
        VoteAverage = tv.VoteAverage;

        Genres = tv.Genres.Select(g => new GenreDto
        {
            Id = g.Id,
            Name = g.Name,
            Link = new($"/genres/{g.Id}", UriKind.Relative),
        });

        Rating = new()
        {
            Rating = tv.CertificationRating.OrEmpty(),
            Iso31661 = tv.CertificationCountry.OrEmpty(),
        };

        NumberOfItems = tv.NumberOfEpisodes;
        HaveItems = tv.HaveEpisodes;
        Duration = tv.Duration * tv.HaveEpisodes * 60 ?? 0;
        TotalDuration = tv.EpisodeDurations.Sum(d => d?.ToSeconds() ?? 0);
        VideoId = tv.Trailer;

        Cast = tv.Cast.Select(c => new PeopleDto
        {
            Id = c.PersonId,
            Name = c.PersonName,
            ProfilePath = c.PersonProfile,
            KnownForDepartment = c.PersonKnownForDepartment,
            ColorPalette = ColorPalette.FromJsonOrNull(c.PersonColorPalette),
            DeathDay = c.PersonDeathDay,
            Gender = c.PersonGender,
            Character = c.Character,
            Order = c.Order,
            Link = new($"/person/{c.PersonId}", UriKind.Relative),
            Translations = [],
        });

        Crew = tv.Crew.Select(c => new PeopleDto
        {
            Id = c.PersonId,
            Name = c.PersonName,
            ProfilePath = c.PersonProfile,
            KnownForDepartment = c.PersonKnownForDepartment,
            ColorPalette = ColorPalette.FromJsonOrNull(c.PersonColorPalette),
            DeathDay = c.PersonDeathDay,
            Gender = c.PersonGender,
            Job = c.Task,
            Order = c.Order,
            Link = new($"/person/{c.PersonId}", UriKind.Relative),
            Translations = [],
        });
    }
}
