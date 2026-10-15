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
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// A plugin asks for one library's shows or movies. An id that is not a
/// library id must answer nothing, never every library.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginLibraryQueryFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Ulid _shows = Ulid.NewUlid();
    private readonly Ulid _otherShows = Ulid.NewUlid();

    public PluginLibraryQueryFilterTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();

        using SqliteCommand relax = _connection.CreateCommand();
        relax.CommandText = "PRAGMA foreign_keys = OFF;";
        relax.ExecuteNonQuery();

        context.Libraries.AddRange(
            new Library { Id = _shows, Title = "Shows" },
            new Library { Id = _otherShows, Title = "Other" }
        );
        context.Tvs.AddRange(
            new Tv
            {
                Id = 1,
                Title = "Mine",
                TitleSort = "mine",
                LibraryId = _shows,
            },
            new Tv
            {
                Id = 2,
                Title = "Theirs",
                TitleSort = "theirs",
                LibraryId = _otherShows,
            }
        );
        context.Movies.AddRange(
            new Movie
            {
                Id = 10,
                Title = "My film",
                LibraryId = _shows,
            },
            new Movie
            {
                Id = 11,
                Title = "Their film",
                LibraryId = _otherShows,
            }
        );
        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData("not-a-library")]
    [InlineData("12345")]
    public async Task An_id_that_is_not_a_library_id_answers_no_shows(string libraryId)
    {
        (await Query().GetShowsAsync(libraryId)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("not-a-library")]
    [InlineData("12345")]
    public async Task An_id_that_is_not_a_library_id_answers_no_movies(string libraryId)
    {
        (await Query().GetMoviesAsync(libraryId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_library_id_answers_that_librarys_items_only()
    {
        IReadOnlyList<PluginLibraryShow> shows = await Query().GetShowsAsync(_shows.ToString());
        IReadOnlyList<PluginLibraryMovie> movies = await Query().GetMoviesAsync(_shows.ToString());

        shows.Select(show => show.Title).Should().Equal("Mine");
        movies.Select(movie => movie.Title).Should().Equal("My film");
    }

    /// <summary>No id at all is the plugin asking for every library, as before.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task No_id_answers_every_library(string? libraryId)
    {
        (await Query().GetShowsAsync(libraryId)).Should().HaveCount(2);
        (await Query().GetMoviesAsync(libraryId)).Should().HaveCount(2);
    }

    private PluginLibraryQuery Query()
    {
        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        return new(factory.Object);
    }
}
