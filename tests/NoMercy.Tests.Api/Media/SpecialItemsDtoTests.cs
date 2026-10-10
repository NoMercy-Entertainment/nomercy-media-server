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
using NoMercy.Api.DTOs.Media;
using NoMercy.Data.DTOs.Specials;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.People;
using NoMercy.Database.Models.TvShows;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Unit")]
public class SpecialItemsDtoTests
{
    private static Cast BuildCast(
        int personId,
        string name,
        string character,
        int order,
        string? colorPaletteJson = null
    )
    {
        return new()
        {
            Person = new()
            {
                Id = personId,
                Name = name,
                Profile = $"/person-{personId}.jpg",
                KnownForDepartment = "Acting",
                Gender = "Female",
                _colorPalette = colorPaletteJson,
            },
            Role = new() { Character = character, Order = order },
        };
    }

    private static Crew BuildCrew(int personId, string name, string task, int order)
    {
        return new()
        {
            Person = new()
            {
                Id = personId,
                Name = name,
                Profile = $"/person-{personId}.jpg",
                KnownForDepartment = "Directing",
                Gender = "Male",
            },
            Job = new() { Task = task, Order = order },
        };
    }

    private static Movie BuildMovie()
    {
        Movie movie = new()
        {
            Id = 501,
            Title = "Movie Title",
            Overview = "Movie overview text.",
            Backdrop = "/movie-backdrop.jpg",
            Poster = "/movie-poster.jpg",
            ReleaseDate = new(2015, 6, 1),
            Runtime = 125,
            VoteAverage = 7.8,
            Video = "yt-key-1",
            ColorPalette = new() { Poster = new() { Dominant = "#111111" } },
        };

        movie.Images.Add(new() { Type = "logo", FilePath = "/logo.png" });
        movie.Images.Add(
            new()
            {
                Type = "backdrop",
                FilePath = "/bd1.jpg",
                Site = "https://image.tmdb.org/t/p/",
                Width = 100,
                Height = 50,
                Iso6391 = "en",
                VoteAverage = 1,
                VoteCount = 1,
            }
        );
        movie.Images.Add(
            new()
            {
                Type = "backdrop",
                FilePath = "/bd2.jpg",
                Site = "local",
                Width = 200,
                Height = 100,
            }
        );
        movie.Images.Add(
            new()
            {
                Type = "poster",
                FilePath = "/p1.jpg",
                Site = "https://image.tmdb.org/t/p/",
            }
        );
        movie.Images.Add(
            new()
            {
                Type = "poster",
                FilePath = "/p2.jpg",
                Site = "local",
            }
        );

        Genre actionGenre = new() { Id = 1, Name = "Action" };
        Genre dramaGenre = new() { Id = 2, Name = "Drama" };
        movie.GenreMovies.Add(
            new()
            {
                GenreId = 1,
                Genre = actionGenre,
                MovieId = movie.Id,
            }
        );
        movie.GenreMovies.Add(
            new()
            {
                GenreId = 2,
                Genre = dramaGenre,
                MovieId = movie.Id,
            }
        );

        Certification certification = new()
        {
            Id = 9,
            Iso31661 = "US",
            Rating = "R",
            Meaning = "Restricted",
            Order = 5,
        };
        movie.CertificationMovies.Add(
            new()
            {
                CertificationId = certification.Id,
                Certification = certification,
                MovieId = movie.Id,
            }
        );

        movie.VideoFiles.Add(new() { Filename = "movie.mkv", HostFolder = "/x" });

        movie.Cast.Add(BuildCast(1, "Actor One", "Hero", 1));
        movie.Cast.Add(BuildCast(2, "Actor Two", "Sidekick", 2));

        movie.Crew.Add(BuildCrew(3, "Director One", "Director", 1));
        movie.Crew.Add(BuildCrew(4, "Writer One", "Writer", 2));

        return movie;
    }

    [Fact]
    public void Ctor_Movie_MapsCoreFieldsAndComputesDuration()
    {
        Movie movie = BuildMovie();

        SpecialItemsDto dto = new(movie);

        Assert.Equal(501, dto.Id);
        Assert.Empty(dto.EpisodeIds);
        Assert.Equal("Movie Title", dto.Title);
        Assert.Equal("Movie overview text.", dto.Overview);
        Assert.Equal("/movie-backdrop.jpg", dto.Backdrop);
        Assert.Equal("/logo.png", dto.Logo);
        Assert.Equal("movie", dto.MediaType);
        Assert.Equal("movie", dto.Type);
        Assert.Equal("/movie/501", dto.Link.ToString());
        Assert.Equal(2015, dto.Year);
        Assert.Equal(7500, dto.Duration);
        Assert.Equal(7500, dto.TotalDuration);
        Assert.Equal(7.8, dto.VoteAverage);
        Assert.Equal(1, dto.NumberOfItems);
        Assert.Equal(1, dto.HaveItems);
        Assert.Equal("yt-key-1", dto.VideoId);
        dto.ColorPalette!.Poster!.Dominant.Should().Be("#111111");

        dto.Genres.Should().HaveCount(2);
        Assert.Equal(1, (int)dto.Genres.First().Id);
        Assert.Equal("Action", dto.Genres.First().Name);
        Assert.Equal("/genres/1", dto.Genres.First().Link.ToString());

        Assert.Equal("R", dto.Rating.Rating);
        Assert.Equal("US", dto.Rating.Iso31661);
        Assert.Equal("Restricted", dto.Rating.Meaning);

        dto.Backdrops.Should().HaveCount(2);
        ImageDto[] backdrops = dto.Backdrops.ToArray();
        Assert.Equal("/bd1.jpg", backdrops[0].Src);
        Assert.Equal("/images/music/bd2.jpg", backdrops[1].Src);

        dto.Posters.Should().HaveCount(2);
        ImageDto[] posters = dto.Posters.ToArray();
        Assert.Equal("/p1.jpg", posters[0].Src);
        Assert.Equal("/images/music/p2.jpg", posters[1].Src);

        dto.Cast.Should().HaveCount(2);
        dto.Crew.Should().HaveCount(2);
        Assert.Equal("Hero", dto.Cast.First().Character);
        Assert.Equal("Director", dto.Crew.First().Job);
    }

    [Fact]
    public void Ctor_Movie_TruncatesBackdropsPostersCastCrew_ToConfiguredLimits()
    {
        Movie movie = BuildMovie();
        movie.Images.Add(new() { Type = "backdrop", FilePath = "/bd3.jpg" });
        movie.Images.Add(new() { Type = "poster", FilePath = "/p3.jpg" });

        for (int i = 5; i <= 20; i++)
            movie.Cast.Add(BuildCast(i, $"Actor {i}", "Extra", i));
        for (int i = 5; i <= 20; i++)
            movie.Crew.Add(BuildCrew(i, $"Crew {i}", "Grip", i));

        SpecialItemsDto dto = new(movie);

        dto.Backdrops.Should().HaveCount(2);
        dto.Posters.Should().HaveCount(2);
        dto.Cast.Should().HaveCount(15);
        dto.Crew.Should().HaveCount(15);
    }

    [Fact]
    public void Ctor_Movie_NoRuntimeNoVideoFilesNoCertifications_ZerosOutFields()
    {
        Movie movie = new()
        {
            Id = 502,
            Title = "Bare Movie",
            Runtime = null,
        };

        SpecialItemsDto dto = new(movie);

        Assert.Equal(0, dto.Duration);
        Assert.Equal(0, dto.TotalDuration);
        Assert.Equal(0, dto.HaveItems);
        dto.Rating.Should().BeEquivalentTo(new Certification());
        dto.Backdrops.Should().BeEmpty();
        dto.Posters.Should().BeEmpty();
        Assert.Null(dto.Logo);
    }

    private static Episode BuildEpisode(
        int id,
        int seasonNumber,
        string? duration,
        bool hasVideoFile
    )
    {
        Episode episode = new() { Id = id, SeasonNumber = seasonNumber };

        if (hasVideoFile)
            episode.VideoFiles.Add(
                new()
                {
                    Filename = $"ep{id}.mkv",
                    HostFolder = "/x",
                    Duration = duration,
                }
            );

        return episode;
    }

    [Fact]
    public void Ctor_Tv_MapsCoreFieldsAndFiltersSeasonsForCounts()
    {
        Tv tv = new()
        {
            Id = 900,
            Title = "Show Title",
            Overview = "Show overview",
            Backdrop = "/tv-backdrop.jpg",
            Poster = "/tv-poster.jpg",
            FirstAirDate = new(2018, 3, 1),
            Duration = 10,
            VoteAverage = 8.2,
            Trailer = "yt-trailer-1",
        };

        Episode episodeA = BuildEpisode(1, 1, "00:10:00", hasVideoFile: true);
        Episode episodeB = BuildEpisode(2, 1, null, hasVideoFile: false);
        Episode episodeC = BuildEpisode(3, 0, "00:05:00", hasVideoFile: true);
        Episode episodeE = BuildEpisode(4, 1, null, hasVideoFile: true);

        tv.Episodes.Add(episodeA);
        tv.Episodes.Add(episodeB);
        tv.Episodes.Add(episodeC);
        tv.Episodes.Add(episodeE);

        Genre genre = new() { Id = 7, Name = "Sci-Fi" };
        tv.GenreTvs.Add(
            new()
            {
                GenreId = 7,
                Genre = genre,
                TvId = tv.Id,
            }
        );

        Certification certification = new()
        {
            Id = 3,
            Iso31661 = "NL",
            Rating = "12",
            Meaning = "Twelve and up",
        };
        tv.CertificationTvs.Add(
            new()
            {
                CertificationId = certification.Id,
                Certification = certification,
                TvId = tv.Id,
            }
        );

        tv.Cast.Add(BuildCast(10, "TV Actor", "Lead", 1));
        tv.Crew.Add(BuildCrew(11, "TV Director", "Director", 1));

        SpecialItemsDto dto = new(tv);

        Assert.Equal(900, dto.Id);
        Assert.Equal([1, 2, 3, 4], dto.EpisodeIds);
        Assert.Equal("Show Title", dto.Title);
        Assert.Equal("tv", dto.MediaType);
        Assert.Equal("tv", dto.Type);
        Assert.Equal("/tv/900", dto.Link.ToString());
        Assert.Equal(2018, dto.Year);
        Assert.Equal(8.2, dto.VoteAverage);
        Assert.Equal("yt-trailer-1", dto.VideoId);

        // NumberOfItems / HaveItems only count SeasonNumber > 0 episodes (A, B, E).
        Assert.Equal(3, dto.NumberOfItems);
        // Only A and E have video files among the season > 0 episodes.
        Assert.Equal(2, dto.HaveItems);
        Assert.Equal(1200, dto.Duration); // tv.Duration(10) * have(2) * 60

        // TotalDuration sums over ALL episodes regardless of season, null-safe.
        Assert.Equal(900, dto.TotalDuration); // 600 (A) + 0 (B, no file) + 300 (C) + 0 (E, null duration)

        Assert.Equal("12", dto.Rating.Rating);
        Assert.Equal("NL", dto.Rating.Iso31661);

        dto.Genres.Should().ContainSingle();
        dto.Cast.Should().ContainSingle();
        dto.Crew.Should().ContainSingle();
    }

    [Fact]
    public void Ctor_Tv_NoCertificationsAndNullDuration_FallsBackToDefaults()
    {
        Tv tv = new()
        {
            Id = 901,
            Title = "Bare Show",
            Duration = null,
        };
        tv.Episodes.Add(BuildEpisode(1, 1, "00:01:00", hasVideoFile: true));

        SpecialItemsDto dto = new(tv);

        Assert.Equal(0, dto.Duration);
        dto.Rating.Should().BeEquivalentTo(new Certification());
    }

    private static SpecialMovieProjection BuildMovieProjection()
    {
        string colorPaletteJson = JsonConvert.SerializeObject(
            new ColorPalette { Poster = new() { Dominant = "#abcdef" } }
        );

        return new()
        {
            Id = 10,
            Title = "Proj Movie",
            Overview = "proj overview",
            Backdrop = "/pmb.jpg",
            Poster = "/pmp.jpg",
            Logo = "/logo-proj.png",
            ColorPalette = colorPaletteJson,
            ReleaseDate = new(2010, 1, 1),
            Runtime = 100,
            VoteAverage = 6.5,
            Video = "v-key",
            VideoFileCount = 2,
            CertificationRating = "PG",
            CertificationCountry = "US",
            Genres = [new() { Id = 5, Name = "Sci-Fi" }],
            Backdrops =
            [
                new()
                {
                    Id = 1,
                    Site = "https://image.tmdb.org/t/p/",
                    FilePath = "/bd-tmdb.jpg",
                    Width = 200,
                    Height = 100,
                    Type = "backdrop",
                    Iso6391 = "en",
                    VoteAverage = 5,
                    VoteCount = 10,
                    ColorPalette = colorPaletteJson,
                },
                new()
                {
                    Id = 2,
                    Site = "local",
                    FilePath = "/bd-local.jpg",
                    Width = 300,
                    Height = 150,
                    Type = "backdrop",
                },
            ],
            Posters =
            [
                new()
                {
                    Id = 3,
                    Site = "https://image.tmdb.org/t/p/",
                    FilePath = "/p-tmdb.jpg",
                    Type = "poster",
                },
                new()
                {
                    Id = 4,
                    Site = "local",
                    FilePath = "/p-local.jpg",
                    Type = "poster",
                },
            ],
            Cast =
            [
                new()
                {
                    PersonId = 1,
                    PersonName = "Actor One",
                    PersonProfile = "/actor1.jpg",
                    PersonKnownForDepartment = "Acting",
                    PersonColorPalette = colorPaletteJson,
                    PersonGender = "Female",
                    Character = "Hero",
                    Order = 1,
                },
            ],
            Crew =
            [
                new()
                {
                    PersonId = 2,
                    PersonName = "Crew One",
                    PersonProfile = "/crew1.jpg",
                    PersonKnownForDepartment = "Directing",
                    PersonGender = "Male",
                    Task = "Director",
                    Order = 1,
                },
            ],
        };
    }

    [Fact]
    public void Ctor_SpecialMovieProjection_MapsAllFieldsWithImageSiteBranching()
    {
        SpecialMovieProjection projection = BuildMovieProjection();

        SpecialItemsDto dto = new(projection);

        Assert.Equal(10, dto.Id);
        Assert.Empty(dto.EpisodeIds);
        Assert.Equal("Proj Movie", dto.Title);
        Assert.Equal("/logo-proj.png", dto.Logo);
        Assert.Equal("movie", dto.MediaType);
        Assert.Equal("/movie/10", dto.Link.ToString());
        Assert.Equal(2010, dto.Year);
        Assert.Equal(6000, dto.Duration);
        Assert.Equal(6000, dto.TotalDuration);
        Assert.Equal(6.5, dto.VoteAverage);
        Assert.Equal(1, dto.NumberOfItems);
        Assert.Equal(1, dto.HaveItems);
        Assert.Equal("v-key", dto.VideoId);
        dto.ColorPalette!.Poster!.Dominant.Should().Be("#abcdef");

        Assert.Equal("PG", dto.Rating.Rating);
        Assert.Equal("US", dto.Rating.Iso31661);

        dto.Genres.Should().ContainSingle();
        Assert.Equal(5, (int)dto.Genres.First().Id);
        Assert.Equal("/genres/5", dto.Genres.First().Link.ToString());

        ImageDto[] backdrops = dto.Backdrops.ToArray();
        Assert.Equal("/bd-tmdb.jpg", backdrops[0].Src);
        Assert.Equal("/images/music/bd-local.jpg", backdrops[1].Src);
        backdrops[0].ColorPalette!.Poster!.Dominant.Should().Be("#abcdef");

        ImageDto[] posters = dto.Posters.ToArray();
        Assert.Equal("/p-tmdb.jpg", posters[0].Src);
        Assert.Equal("/images/music/p-local.jpg", posters[1].Src);

        PeopleDto cast = dto.Cast.Single();
        Assert.Equal(1, cast.Id);
        Assert.Equal("Actor One", cast.Name);
        Assert.Equal("Hero", cast.Character);
        Assert.Equal("/person/1", cast.Link.ToString());
        Assert.Empty(cast.Translations);

        PeopleDto crew = dto.Crew.Single();
        Assert.Equal("Director", crew.Job);
        Assert.Equal("/person/2", crew.Link.ToString());
    }

    [Fact]
    public void Ctor_SpecialMovieProjection_NullsFallBackToEmptyAndZero()
    {
        SpecialMovieProjection projection = BuildMovieProjection();
        projection.CertificationRating = null;
        projection.CertificationCountry = null;
        projection.Runtime = null;
        projection.VideoFileCount = 0;
        projection.ColorPalette = string.Empty;

        SpecialItemsDto dto = new(projection);

        Assert.Equal(string.Empty, dto.Rating.Rating);
        Assert.Equal(string.Empty, dto.Rating.Iso31661);
        Assert.Equal(0, dto.Duration);
        Assert.Equal(0, dto.HaveItems);
        Assert.Null(dto.ColorPalette);
    }

    private static SpecialTvProjection BuildTvProjection()
    {
        string colorPaletteJson = JsonConvert.SerializeObject(
            new ColorPalette { Poster = new() { Dominant = "#fedcba" } }
        );

        return new()
        {
            Id = 20,
            Title = "Proj Show",
            Overview = "proj tv overview",
            Backdrop = "/ptb.jpg",
            Poster = "/ptp.jpg",
            Logo = "/logo-proj-tv.png",
            ColorPalette = colorPaletteJson,
            FirstAirDate = new(2012, 4, 1),
            Duration = 20,
            VoteAverage = 7.1,
            Trailer = "tv-key",
            NumberOfEpisodes = 10,
            HaveEpisodes = 3,
            EpisodeIds = [101, 102, 103],
            EpisodeDurations = ["00:20:00", null, "00:10:00"],
            CertificationRating = "16",
            CertificationCountry = "NL",
            Genres = [new() { Id = 6, Name = "Drama" }],
            Backdrops =
            [
                new()
                {
                    Id = 1,
                    Site = "https://image.tmdb.org/t/p/",
                    FilePath = "/tv-bd-tmdb.jpg",
                    Type = "backdrop",
                },
                new()
                {
                    Id = 2,
                    Site = "local",
                    FilePath = "/tv-bd-local.jpg",
                    Type = "backdrop",
                },
            ],
            Posters =
            [
                new()
                {
                    Id = 3,
                    Site = "https://image.tmdb.org/t/p/",
                    FilePath = "/tv-p-tmdb.jpg",
                    Type = "poster",
                },
                new()
                {
                    Id = 4,
                    Site = "local",
                    FilePath = "/tv-p-local.jpg",
                    Type = "poster",
                },
            ],
            Cast =
            [
                new()
                {
                    PersonId = 5,
                    PersonName = "TV Actor",
                    PersonGender = "Female",
                    Character = "Lead",
                    Order = 1,
                },
            ],
            Crew =
            [
                new()
                {
                    PersonId = 6,
                    PersonName = "TV Crew",
                    PersonGender = "Male",
                    Task = "Showrunner",
                    Order = 1,
                },
            ],
        };
    }

    [Fact]
    public void Ctor_SpecialTvProjection_MapsAllFieldsDirectlyFromProjection()
    {
        SpecialTvProjection projection = BuildTvProjection();

        SpecialItemsDto dto = new(projection);

        Assert.Equal(20, dto.Id);
        Assert.Equal([101, 102, 103], dto.EpisodeIds);
        Assert.Equal("Proj Show", dto.Title);
        Assert.Equal("tv", dto.MediaType);
        Assert.Equal("/tv/20", dto.Link.ToString());
        Assert.Equal(2012, dto.Year);
        Assert.Equal(7.1, dto.VoteAverage);
        Assert.Equal("tv-key", dto.VideoId);
        Assert.Equal(10, dto.NumberOfItems);
        Assert.Equal(3, dto.HaveItems);
        Assert.Equal(3600, dto.Duration); // 20 * 3 * 60

        // EpisodeDurations: 1200 + 0 (null) + 600 = 1800, null-safe sum.
        Assert.Equal(1800, dto.TotalDuration);

        Assert.Equal("16", dto.Rating.Rating);
        Assert.Equal("NL", dto.Rating.Iso31661);

        ImageDto[] backdrops = dto.Backdrops.ToArray();
        Assert.Equal("/tv-bd-tmdb.jpg", backdrops[0].Src);
        Assert.Equal("/images/music/tv-bd-local.jpg", backdrops[1].Src);

        ImageDto[] posters = dto.Posters.ToArray();
        Assert.Equal("/tv-p-tmdb.jpg", posters[0].Src);
        Assert.Equal("/images/music/tv-p-local.jpg", posters[1].Src);

        Assert.Equal("Lead", dto.Cast.Single().Character);
        Assert.Equal("Showrunner", dto.Crew.Single().Job);
    }

    [Fact]
    public void Ctor_SpecialTvProjection_NullDurationAndCertifications_FallsBackToDefaults()
    {
        SpecialTvProjection projection = BuildTvProjection();
        projection.Duration = null;
        projection.CertificationRating = null;
        projection.CertificationCountry = null;
        projection.EpisodeDurations = [null, null];

        SpecialItemsDto dto = new(projection);

        Assert.Equal(0, dto.Duration);
        Assert.Equal(0, dto.TotalDuration);
        Assert.Equal(string.Empty, dto.Rating.Rating);
        Assert.Equal(string.Empty, dto.Rating.Iso31661);
    }
}
