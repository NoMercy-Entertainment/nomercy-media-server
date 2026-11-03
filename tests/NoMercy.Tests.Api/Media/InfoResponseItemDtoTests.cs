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

using NoMercy.Api.DTOs.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.People;
using NoMercy.Database.Models.TvShows;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Providers.TMDB.Models.TV;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Unit")]
public class InfoResponseItemDtoTests
{
    private static Cast BuildCast(int personId, int? order)
    {
        return new()
        {
            PersonId = personId,
            Person = new() { Id = personId, Name = $"Actor {personId}" },
            Role = new() { Character = $"Character {personId}", Order = order },
        };
    }

    private static Crew BuildCrew(int personId, int? order, string task = "Producer")
    {
        return new()
        {
            PersonId = personId,
            Person = new() { Id = personId, Name = $"Crew {personId}" },
            Job = new()
            {
                Task = task,
                Order = order,
                CreditId = $"credit-{personId}",
            },
        };
    }

    private static TmdbCast BuildTmdbCast(int id, int? order)
    {
        return new()
        {
            Id = id,
            Name = $"Actor {id}",
            Gender = 1,
            Order = order,
        };
    }

    private static TmdbCrew BuildTmdbCrew(int id, int? order)
    {
        return new()
        {
            Id = id,
            Name = $"Crew {id}",
            Gender = 1,
            Job = "Producer",
            Order = order,
        };
    }

    [Fact]
    public void Ctor_FromMovie_SortsCastAndCrewByRoleOrder()
    {
        Movie movie = new()
        {
            Id = 1,
            Title = "Movie",
            Cast = [BuildCast(10, 2), BuildCast(11, 0), BuildCast(12, 1)],
            Crew = [BuildCrew(20, 2), BuildCrew(21, 0), BuildCrew(22, 1)],
        };

        InfoResponseItemDto dto = new(movie, "US");

        Assert.Equal([11, 12, 10], dto.Cast.Select(people => people.Id).ToArray());
        Assert.Equal([21, 22, 20], dto.Crew.Select(people => people.Id).ToArray());
    }

    [Fact]
    public void Ctor_FromMovie_PutsCastAndCrewWithoutOrderLast()
    {
        Movie movie = new()
        {
            Id = 1,
            Title = "Movie",
            Cast = [BuildCast(10, null), BuildCast(11, 1), BuildCast(12, 0)],
            Crew = [BuildCrew(20, null), BuildCrew(21, 1), BuildCrew(22, 0)],
        };

        InfoResponseItemDto dto = new(movie, "US");

        Assert.Equal([12, 11, 10], dto.Cast.Select(people => people.Id).ToArray());
        Assert.Equal([22, 21, 20], dto.Crew.Select(people => people.Id).ToArray());
    }

    [Fact]
    public void Ctor_FromTv_SortsCastAndCrewByRoleOrder()
    {
        Tv tv = new()
        {
            Id = 1,
            Title = "Show",
            Cast = [BuildCast(10, 2), BuildCast(11, 0), BuildCast(12, 1)],
            Crew = [BuildCrew(20, 2), BuildCrew(21, 0), BuildCrew(22, 1)],
        };

        InfoResponseItemDto dto = new(tv, "US");

        Assert.Equal([11, 12, 10], dto.Cast.Select(people => people.Id).ToArray());
        Assert.Equal([21, 22, 20], dto.Crew.Select(people => people.Id).ToArray());
    }

    [Fact]
    public void Ctor_FromTv_SortsEpisodeCastTogetherWithShowCast()
    {
        Episode episode = new()
        {
            Id = 100,
            Cast = [BuildCast(13, 0)],
            Crew = [BuildCrew(23, 0)],
        };

        Tv tv = new()
        {
            Id = 1,
            Title = "Show",
            Episodes = [episode],
            Cast = [BuildCast(10, 2), BuildCast(11, null), BuildCast(12, 1)],
            Crew = [BuildCrew(20, 2), BuildCrew(21, null), BuildCrew(22, 1)],
        };

        InfoResponseItemDto dto = new(tv, "US");

        Assert.Equal([13, 12, 10, 11], dto.Cast.Select(people => people.Id).ToArray());
        Assert.Equal([23, 22, 20, 21], dto.Crew.Select(people => people.Id).ToArray());
    }

    [Fact]
    public void Ctor_FromTmdbMovie_SortsCastAndCrewByOrder()
    {
        TmdbMovieAppends tmdbMovie = new()
        {
            Id = 1,
            Title = "Movie",
            Credits = new()
            {
                Cast = [BuildTmdbCast(10, 2), BuildTmdbCast(11, 0), BuildTmdbCast(12, 1)],
                Crew = [BuildTmdbCrew(20, 2), BuildTmdbCrew(21, 0), BuildTmdbCrew(22, 1)],
            },
        };

        InfoResponseItemDto dto = new(tmdbMovie, "US");

        Assert.Equal([11, 12, 10], dto.Cast.Select(people => people.Id).ToArray());
        Assert.Equal([21, 22, 20], dto.Crew.Select(people => people.Id).ToArray());
    }

    [Fact]
    public void Ctor_FromTmdbTv_SortsCastAndCrewByOrder()
    {
        TmdbTvShowAppends tmdbTv = new()
        {
            Id = 1,
            Name = "Show",
            Credits = new()
            {
                Cast = [BuildTmdbCast(10, 2), BuildTmdbCast(11, 0), BuildTmdbCast(12, 1)],
                Crew = [BuildTmdbCrew(20, 2), BuildTmdbCrew(21, 0), BuildTmdbCrew(22, 1)],
            },
        };

        InfoResponseItemDto dto = new(tmdbTv, "US");

        Assert.Equal([11, 12, 10], dto.Cast.Select(people => people.Id).ToArray());
        Assert.Equal([21, 22, 20], dto.Crew.Select(people => people.Id).ToArray());
    }
}
