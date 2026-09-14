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
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using Xunit;

namespace NoMercy.Tests.Repositories;

public class AnimeThemeRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AnimeThemeRepositoryTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();

        using SqliteCommand relax = _connection.CreateCommand();
        relax.CommandText = "PRAGMA foreign_keys = OFF;";
        relax.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<bool> AnyThemedTitlesAsync()
    {
        await using MediaContext context = new(_options);
        return await new AnimeThemeRepository(context).AnyThemedTitlesAsync();
    }

    [Fact]
    public async Task AnyThemedTitles_NoLinks_IsFalse()
    {
        (await AnyThemedTitlesAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AnyThemedTitles_AShowLink_IsTrue()
    {
        await using (MediaContext context = new(_options))
        {
            context.AnimeThemeTv.Add(new AnimeThemeTv { AnimeThemeId = 1, TvId = 1429 });
            await context.SaveChangesAsync();
        }

        (await AnyThemedTitlesAsync()).Should().BeTrue();
    }

    // The adult-content query filter joins the movie, so the linked movie must exist.
    [Fact]
    public async Task AnyThemedTitles_OnlyAMovieLink_IsTrue()
    {
        await using (MediaContext context = new(_options))
        {
            context.Movies.Add(
                new Movie
                {
                    Id = 129,
                    Title = "Spirited Away",
                    LibraryId = Ulid.NewUlid(),
                }
            );
            context.AnimeThemeMovie.Add(new AnimeThemeMovie { AnimeThemeId = 1, MovieId = 129 });
            await context.SaveChangesAsync();
        }

        (await AnyThemedTitlesAsync()).Should().BeTrue();
    }
}
