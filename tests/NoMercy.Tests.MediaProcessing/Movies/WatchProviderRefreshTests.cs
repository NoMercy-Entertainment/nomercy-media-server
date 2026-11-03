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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Movies;
using NoMercy.MediaProcessing.Shows;

namespace NoMercy.Tests.MediaProcessing.Movies;

public sealed class WatchProviderRefreshTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<MediaContext> _options;

    public WatchProviderRefreshTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Library",
            Type = "movie",
        };
        context.Libraries.Add(library);
        context.Movies.Add(
            new Movie
            {
                Id = 1,
                Title = "Movie",
                LibraryId = library.Id,
            }
        );
        context.Tvs.Add(
            new Tv
            {
                Id = 2,
                Title = "Show",
                LibraryId = library.Id,
            }
        );
        context.WatchProviders.Add(new WatchProvider { Id = 7, Name = "Provider" });
        context.SaveChanges();
    }

    [Fact]
    public async Task RepeatedMovieRefreshKeepsOneProviderRow()
    {
        await using MediaContext context = new(_options);
        MovieRepository repository = new(context);
        await repository.StoreWatchProviderMedias([MovieRow("first")]);
        await repository.StoreWatchProviderMedias([MovieRow("second")]);

        List<WatchProviderMedia> rows = await context
            .WatchProviderMedia.AsNoTracking()
            .Where(row => row.MovieId == 1)
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal("second", rows[0].Link);
    }

    [Fact]
    public async Task RepeatedShowRefreshKeepsOneProviderRow()
    {
        await using MediaContext context = new(_options);
        ShowRepository repository = new(context);
        await repository.StoreWatchProviderMedias([ShowRow("first")]);
        await repository.StoreWatchProviderMedias([ShowRow("second")]);

        List<WatchProviderMedia> rows = await context
            .WatchProviderMedia.AsNoTracking()
            .Where(row => row.TvId == 2)
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal("second", rows[0].Link);
    }

    private static WatchProviderMedia MovieRow(string link) =>
        new()
        {
            WatchProviderId = 7,
            CountryCode = "US",
            ProviderType = "flatrate",
            MovieId = 1,
            Link = link,
        };

    private static WatchProviderMedia ShowRow(string link) =>
        new()
        {
            WatchProviderId = 7,
            CountryCode = "US",
            ProviderType = "flatrate",
            TvId = 2,
            Link = link,
        };

    public void Dispose() => _connection.Dispose();
}
