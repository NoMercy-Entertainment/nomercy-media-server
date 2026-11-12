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
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;

namespace NoMercy.Tests.Repositories;

// AUD-1115: genre rows list only titles from libraries the user can access.
public class HomeGenreLibraryScopeTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Ulid AllowedLibraryId = Ulid.NewUlid();
    private static readonly Ulid OtherLibraryId = Ulid.NewUlid();

    private const int AllowedMovie = 301;
    private const int OtherMovie = 302;
    private const int AllowedShow = 401;
    private const int OtherShow = 402;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public HomeGenreLibraryScopeTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetHome_GenreRows_ListOnlyTitlesFromAccessibleLibraries()
    {
        await SeedAsync();
        HomeRepository repository = new(new MediaContext(_options), new Factory(_options));

        List<Genre> genres = await repository.GetHome(UserId, "en", take: 10);

        Genre genre = genres.Should().ContainSingle().Subject;
        genre.GenreMovies.Select(gm => gm.MovieId).Should().BeEquivalentTo(new[] { AllowedMovie });
        genre.GenreTvShows.Select(gt => gt.TvId).Should().BeEquivalentTo(new[] { AllowedShow });
    }

    private async Task SeedAsync()
    {
        await using MediaContext context = new(_options);

        context.Users.Add(new() { Id = UserId, Email = "user@example.com" });
        context.Libraries.AddRange(
            new()
            {
                Id = AllowedLibraryId,
                Title = "Allowed",
                Type = "movie",
            },
            new()
            {
                Id = OtherLibraryId,
                Title = "Other",
                Type = "movie",
            }
        );
        await context.SaveChangesAsync();

        context.LibraryUser.Add(new() { LibraryId = AllowedLibraryId, UserId = UserId });
        context.Genres.Add(new() { Id = 28, Name = "Action" });
        foreach (
            (int id, Ulid library) in new[]
            {
                (AllowedMovie, AllowedLibraryId),
                (OtherMovie, OtherLibraryId),
            }
        )
        {
            context.Movies.Add(
                new()
                {
                    Id = id,
                    Title = $"Movie {id}",
                    TitleSort = $"movie {id}",
                    LibraryId = library,
                }
            );
        }
        foreach (
            (int id, Ulid library) in new[]
            {
                (AllowedShow, AllowedLibraryId),
                (OtherShow, OtherLibraryId),
            }
        )
        {
            context.Tvs.Add(
                new()
                {
                    Id = id,
                    Title = $"Show {id}",
                    TitleSort = $"show {id}",
                    LibraryId = library,
                }
            );
        }
        await context.SaveChangesAsync();

        foreach (int id in new[] { AllowedMovie, OtherMovie })
        {
            context.GenreMovie.Add(new() { GenreId = 28, MovieId = id });
            context.VideoFiles.Add(
                new()
                {
                    Id = Ulid.NewUlid(),
                    MovieId = id,
                    Folder = "/Movies/Movie",
                    Filename = $"movie{id}.mkv",
                    Share = "local",
                }
            );
        }
        foreach (int id in new[] { AllowedShow, OtherShow })
        {
            context.GenreTv.Add(new() { GenreId = 28, TvId = id });
            Season season = new()
            {
                Id = id,
                TvId = id,
                SeasonNumber = 1,
                Title = "Season 1",
            };
            context.Seasons.Add(season);
            await context.SaveChangesAsync();
            Episode episode = new()
            {
                Id = id,
                TvId = id,
                SeasonId = season.Id,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                Title = "One",
            };
            context.Episodes.Add(episode);
            await context.SaveChangesAsync();
            context.VideoFiles.Add(
                new()
                {
                    Id = Ulid.NewUlid(),
                    EpisodeId = episode.Id,
                    Folder = "/Show",
                    Filename = $"show{id}.mkv",
                    Share = "local",
                }
            );
        }
        await context.SaveChangesAsync();
    }

    private sealed class Factory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext()
        {
            return new(options);
        }
    }
}
