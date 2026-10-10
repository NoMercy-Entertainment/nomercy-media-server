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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.People;
using NoMercy.Database.Models.Users;

namespace NoMercy.Tests.Repositories;

public class PeopleRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public PeopleRepositoryTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private MediaContext OpenContext()
    {
        return new(_options);
    }

    [Fact]
    public async Task GetPeopleAsync_OnlyReturnsPeopleCastInALibraryTheUserCanSee()
    {
        Guid memberUserId = Guid.NewGuid();
        Guid strangerUserId = Guid.NewGuid();
        Ulid libraryId = Ulid.NewUlid();

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Users.AddRange(
            new User { Id = memberUserId, Email = "member@example.com" },
            new User { Id = strangerUserId, Email = "stranger@example.com" }
        );
        seedCtx.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Movies",
                Type = "movie",
                Order = 1,
            }
        );
        seedCtx.LibraryUser.Add(new(libraryId, memberUserId));

        Movie movie = new()
        {
            Id = 1,
            Title = "Movie",
            TitleSort = "movie",
            LibraryId = libraryId,
        };
        seedCtx.Movies.Add(movie);

        Person castMember = new()
        {
            Id = 10,
            Name = "Cast Member",
            TitleSort = "cast member",
            Popularity = 5,
        };
        seedCtx.People.Add(castMember);
        seedCtx.Casts.Add(new() { PersonId = castMember.Id, MovieId = movie.Id });

        await seedCtx.SaveChangesAsync();

        await using MediaContext queryCtx = OpenContext();
        PeopleRepository repository = new(queryCtx);

        List<Person> memberResult = await repository.GetPeopleAsync(memberUserId, "en", 10);
        List<Person> strangerResult = await repository.GetPeopleAsync(strangerUserId, "en", 10);

        memberResult.Should().ContainSingle(p => p.Id == castMember.Id);
        strangerResult.Should().BeEmpty("the stranger has no LibraryUser row for this library");
    }

    [Fact]
    public async Task GetPeopleAsync_IncludesOnlyTheRequestedLanguageTranslation()
    {
        Guid userId = Guid.NewGuid();
        Ulid libraryId = Ulid.NewUlid();

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Users.Add(new User { Id = userId, Email = "user@example.com" });
        seedCtx.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Movies",
                Type = "movie",
                Order = 1,
            }
        );
        seedCtx.LibraryUser.Add(new(libraryId, userId));

        Movie movie = new()
        {
            Id = 2,
            Title = "Movie",
            TitleSort = "movie",
            LibraryId = libraryId,
        };
        seedCtx.Movies.Add(movie);

        Person person = new()
        {
            Id = 20,
            Name = "Actor",
            TitleSort = "actor",
            Popularity = 1,
        };
        seedCtx.People.Add(person);
        seedCtx.Casts.Add(new() { PersonId = person.Id, MovieId = movie.Id });
        seedCtx.Translations.AddRange(
            new Translation
            {
                PersonId = person.Id,
                Iso6391 = "en",
                Biography = "English bio",
            },
            new Translation
            {
                PersonId = person.Id,
                Iso6391 = "nl",
                Biography = "Dutch bio",
            }
        );

        await seedCtx.SaveChangesAsync();

        await using MediaContext queryCtx = OpenContext();
        PeopleRepository repository = new(queryCtx);

        List<Person> result = await repository.GetPeopleAsync(userId, "nl", 10);

        Person returned = result.Should().ContainSingle().Subject;
        returned.Translations.Should().ContainSingle();
        returned.Translations.Single().Biography.Should().Be("Dutch bio");
    }

    [Fact]
    public async Task GetPeopleAsync_OrdersByPopularityDescendingThenIdAscending()
    {
        Guid userId = Guid.NewGuid();
        Ulid libraryId = Ulid.NewUlid();

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Users.Add(new User { Id = userId, Email = "user@example.com" });
        seedCtx.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Movies",
                Type = "movie",
                Order = 1,
            }
        );
        seedCtx.LibraryUser.Add(new(libraryId, userId));

        Movie movie = new()
        {
            Id = 3,
            Title = "Movie",
            TitleSort = "movie",
            LibraryId = libraryId,
        };
        seedCtx.Movies.Add(movie);

        // lowId/highId share equal popularity to isolate the ThenBy(Id) tie-break;
        // mostPopular has strictly higher popularity to isolate the primary sort.
        Person mostPopular = new()
        {
            Id = 33,
            Name = "Most Popular",
            TitleSort = "most popular",
            Popularity = 100,
        };
        Person lowId = new()
        {
            Id = 31,
            Name = "Low Id",
            TitleSort = "low id",
            Popularity = 5,
        };
        Person highId = new()
        {
            Id = 32,
            Name = "High Id",
            TitleSort = "high id",
            Popularity = 5,
        };
        seedCtx.People.AddRange(mostPopular, lowId, highId);
        seedCtx.Casts.AddRange(
            new Cast { PersonId = mostPopular.Id, MovieId = movie.Id },
            new Cast { PersonId = lowId.Id, MovieId = movie.Id },
            new Cast { PersonId = highId.Id, MovieId = movie.Id }
        );

        await seedCtx.SaveChangesAsync();

        await using MediaContext queryCtx = OpenContext();
        PeopleRepository repository = new(queryCtx);

        List<Person> result = await repository.GetPeopleAsync(userId, "en", 10);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be(mostPopular.Id, "the highest popularity must sort first");
        result[1]
            .Id.Should()
            .Be(lowId.Id, "of equal popularity, the lower Id must win the tie-break");
        result[2].Id.Should().Be(highId.Id);
    }

    [Fact]
    public async Task GetPeopleAsync_RespectsTakeAndPageForPagination()
    {
        Guid userId = Guid.NewGuid();
        Ulid libraryId = Ulid.NewUlid();

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Users.Add(new User { Id = userId, Email = "user@example.com" });
        seedCtx.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Movies",
                Type = "movie",
                Order = 1,
            }
        );
        seedCtx.LibraryUser.Add(new(libraryId, userId));

        Movie movie = new()
        {
            Id = 4,
            Title = "Movie",
            TitleSort = "movie",
            LibraryId = libraryId,
        };
        seedCtx.Movies.Add(movie);

        for (int i = 0; i < 5; i++)
        {
            Person person = new()
            {
                Id = 40 + i,
                Name = $"Person {i}",
                TitleSort = $"person {i}",
                Popularity = 5 - i,
            };
            seedCtx.People.Add(person);
            seedCtx.Casts.Add(new() { PersonId = person.Id, MovieId = movie.Id });
        }

        await seedCtx.SaveChangesAsync();

        await using MediaContext queryCtx = OpenContext();
        PeopleRepository repository = new(queryCtx);

        List<Person> page0 = await repository.GetPeopleAsync(userId, "en", take: 2, page: 0);
        List<Person> page1 = await repository.GetPeopleAsync(userId, "en", take: 2, page: 1);

        page0.Should().HaveCount(2);
        page1.Should().HaveCount(2);
        page0.Select(p => p.Id).Should().NotIntersectWith(page1.Select(p => p.Id));
        page0[0].Id.Should().Be(40, "most popular person comes first on page 0");
        page1[0].Id.Should().Be(42, "page 1 starts after the first two most-popular people");
    }

    [Fact]
    public async Task GetPersonWithCreditsAsync_UnknownId_ReturnsNull()
    {
        await using MediaContext ctx = OpenContext();
        PeopleRepository repository = new(ctx);

        Person? result = await repository.GetPersonWithCreditsAsync(999);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPersonWithCreditsAsync_IncludesMovieAndTvCastAndCrewCredits()
    {
        Ulid libraryId = Ulid.NewUlid();

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Movies",
                Type = "movie",
                Order = 1,
            }
        );

        Movie castMovie = new()
        {
            Id = 50,
            Title = "Cast Movie",
            TitleSort = "cast movie",
            LibraryId = libraryId,
        };
        Movie crewMovie = new()
        {
            Id = 51,
            Title = "Crew Movie",
            TitleSort = "crew movie",
            LibraryId = libraryId,
        };
        seedCtx.Movies.AddRange(castMovie, crewMovie);

        Person person = new()
        {
            Id = 60,
            Name = "Multi Credit",
            TitleSort = "multi credit",
            Popularity = 1,
        };
        seedCtx.People.Add(person);

        seedCtx.Casts.Add(new() { PersonId = person.Id, MovieId = castMovie.Id });
        seedCtx.Crews.Add(new() { PersonId = person.Id, MovieId = crewMovie.Id });

        await seedCtx.SaveChangesAsync();

        await using MediaContext queryCtx = OpenContext();
        PeopleRepository repository = new(queryCtx);

        Person? result = await repository.GetPersonWithCreditsAsync(person.Id);

        result.Should().NotBeNull();
        result!.Casts.Should().ContainSingle(c => c.MovieId == castMovie.Id);
        result.Crews.Should().ContainSingle(c => c.MovieId == crewMovie.Id);
    }
}
