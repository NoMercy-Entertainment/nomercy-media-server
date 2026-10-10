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
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

// Real-world evidence (Phoenix DB, collection 1776481): CollectionMovies came
// back in row-insertion order -- "Puttin' on the Dog" (1944) before "The Cat
// Concerto" (1947) before "Cat Fishin'" (1947) -- which is neither chronological
// nor stable, and made the 1-based playlist index meaningless. The playlist
// query must order by the movie's release date, then by movie id as a stable
// tiebreaker for movies sharing a date.
[Trait("Category", "Collections")]
public class CollectionPlaylistOrderingTests : IDisposable
{
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;

    public CollectionPlaylistOrderingTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateFactory();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Movie BuildMovie(int id, DateTime? releaseDate, Ulid libraryId)
    {
        Movie movie = new()
        {
            Id = id,
            Title = $"Movie {id}",
            TitleSort = $"movie {id}",
            ReleaseDate = releaseDate,
            LibraryId = libraryId,
        };
        movie.VideoFiles.Add(
            new()
            {
                Filename = $"movie-{id}.mkv",
                Folder = "/movies",
                HostFolder = "/movies",
                Languages = "[\"en\"]",
                Quality = "1080p",
                Share = "movies",
                MovieId = id,
                Movie = movie,
            }
        );
        return movie;
    }

    [Fact]
    public async Task GetCollectionPlaylistAsync_RowsInsertedOutOfOrder_ComesBackInReleaseDateOrder()
    {
        Guid userId = Guid.NewGuid();
        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Movies",
            Type = "movie",
        };

        Collection collection = new()
        {
            Id = 1776481,
            Title = "Anthology",
            LibraryId = library.Id,
            Library = library,
        };

        // Inserted deliberately out of chronological order, matching the real
        // row order found in production.
        Movie puttinOnTheDog = BuildMovie(1, new DateTime(1944, 1, 1), library.Id);
        Movie theCatConcerto = BuildMovie(2, new DateTime(1947, 1, 1), library.Id);
        Movie catFishin = BuildMovie(3, new DateTime(1947, 6, 1), library.Id);
        Movie solidSerenade = BuildMovie(4, new DateTime(1946, 1, 1), library.Id);
        Movie theBodyguard = BuildMovie(5, new DateTime(1944, 6, 1), library.Id);

        await using MediaContext seedContext = await _factory.CreateDbContextAsync();
        seedContext.Users.Add(
            new()
            {
                Id = userId,
                Email = $"{userId}@test.local",
                Name = "Test User",
            }
        );
        seedContext.Libraries.Add(library);
        seedContext.LibraryUser.Add(new(library.Id, userId));
        seedContext.Collections.Add(collection);
        seedContext.Movies.AddRange(
            puttinOnTheDog,
            theCatConcerto,
            catFishin,
            solidSerenade,
            theBodyguard
        );
        seedContext.CollectionMovie.AddRange(
            new CollectionMovie { CollectionId = collection.Id, MovieId = puttinOnTheDog.Id },
            new CollectionMovie { CollectionId = collection.Id, MovieId = theCatConcerto.Id },
            new CollectionMovie { CollectionId = collection.Id, MovieId = catFishin.Id },
            new CollectionMovie { CollectionId = collection.Id, MovieId = solidSerenade.Id },
            new CollectionMovie { CollectionId = collection.Id, MovieId = theBodyguard.Id }
        );
        await seedContext.SaveChangesAsync();

        CollectionRepository repository = new(_factory);

        Collection? result = await repository.GetCollectionPlaylistAsync(
            userId,
            collection.Id,
            "en",
            "US"
        );

        result.Should().NotBeNull();
        result!
            .CollectionMovies.Select(cm => cm.MovieId)
            .Should()
            .Equal(
                puttinOnTheDog.Id, // 1944-01-01
                theBodyguard.Id, // 1944-06-01
                solidSerenade.Id, // 1946-01-01
                theCatConcerto.Id, // 1947-01-01
                catFishin.Id // 1947-06-01
            );
    }
}
