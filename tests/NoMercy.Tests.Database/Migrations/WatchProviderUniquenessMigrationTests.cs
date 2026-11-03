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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Tests.Database.Migrations;

public sealed class WatchProviderUniquenessMigrationTests
{
    private const string PreviousMigration = "20261103015004_AddSpecialOrder";

    [Fact]
    public async Task UpgradeKeepsNewestMovieAndShowProviderRows()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(connection)
            .Options;

        await using (MediaContext before = new(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Library library = new()
            {
                Id = Ulid.NewUlid(),
                Title = "Library",
                Type = "movie",
            };
            before.Libraries.Add(library);
            before.Movies.Add(
                new Movie
                {
                    Id = 1,
                    Title = "Movie",
                    LibraryId = library.Id,
                }
            );
            before.Tvs.Add(
                new Tv
                {
                    Id = 2,
                    Title = "Show",
                    LibraryId = library.Id,
                }
            );
            before.WatchProviders.Add(new WatchProvider { Id = 7, Name = "Provider" });
            await before.SaveChangesAsync();

            foreach ((int? movieId, int? tvId) in new (int?, int?)[] { (1, null), (null, 2) })
            {
                await InsertRow(before, movieId, tvId, "old");
                await InsertRow(before, movieId, tvId, "new");
            }
        }

        await using MediaContext after = new(options);
        await after.Database.MigrateAsync();
        List<WatchProviderMedia> rows = await after.WatchProviderMedia.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("new", row.Link));

        await Assert.ThrowsAsync<SqliteException>(() => InsertRow(after, 1, null, "duplicate"));
        await Assert.ThrowsAsync<SqliteException>(() => InsertRow(after, null, 2, "duplicate"));
    }

    private static Task<int> InsertRow(
        MediaContext context,
        int? movieId,
        int? tvId,
        string link
    ) =>
        context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO WatchProviderMedia (Id, WatchProviderId, CountryCode, ProviderType, MovieId, TvId, Link)
            VALUES ({Ulid.NewUlid().ToString()}, {7}, {"US"}, {"flatrate"}, {movieId}, {tvId}, {link})
            """
        );
}
