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

    [Fact]
    public async Task UpgradeLosesNoOtherRowAndKeepsTheTableShape()
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
            before.Movies.Add(new Movie { Id = 1, Title = "Movie", LibraryId = library.Id });
            before.Tvs.Add(new Tv { Id = 2, Title = "Show", LibraryId = library.Id });
            before.WatchProviders.Add(new WatchProvider { Id = 7, Name = "Provider" });
            await before.SaveChangesAsync();

            await InsertRow(before, 1, null, "old");
            await InsertRow(before, 1, null, "new");
            await InsertRow(before, null, 2, "show");
            await InsertRow(before, 1, 2, "both");
            await before.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO WatchProviderMedia (Id, WatchProviderId, CountryCode, ProviderType, MovieId, TvId, Link)
                VALUES ({Ulid.NewUlid().ToString()}, {7}, {"NL"}, {"flatrate"}, {1}, {null as int?}, {"other country"})
                """
            );
        }

        await using MediaContext after = new(options);
        await after.Database.MigrateAsync();

        List<string?> links = (await after.WatchProviderMedia.AsNoTracking().ToListAsync())
            .Select(row => row.Link)
            .Order()
            .ToList();
        Assert.Equal(new string?[] { "both", "new", "other country", "show" }, links);

        List<string> indexes = await IndexNames(after);
        Assert.Contains("IX_WatchProviderMedia_MovieId", indexes);
        Assert.Contains("IX_WatchProviderMedia_TvId", indexes);
        Assert.Equal(
            2,
            indexes.Count(name => name.Contains("ProviderType_", StringComparison.Ordinal))
        );

        await after.Database.ExecuteSqlAsync($"DELETE FROM Movies WHERE Id = {1}");
        Assert.Equal(1, await after.WatchProviderMedia.CountAsync());
    }

    [Fact]
    public async Task FailureAfterTheTableIsDroppedRollsBackToTheOldTable()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(connection)
            .Options;

        await using MediaContext context = new(options);
        await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Library",
            Type = "movie",
        };
        context.Libraries.Add(library);
        context.Movies.Add(new Movie { Id = 1, Title = "Movie", LibraryId = library.Id });
        context.WatchProviders.Add(new WatchProvider { Id = 7, Name = "Provider" });
        await context.SaveChangesAsync();
        await InsertRow(context, 1, null, "old");
        await InsertRow(context, 1, null, "new");

        // Taken by another table, so the last index create fails after the old table is gone.
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IX_WatchProviderMedia_WatchProviderId_CountryCode_ProviderType_TvId ON WatchProviders (Name)"
        );

        await Assert.ThrowsAsync<SqliteException>(() => context.Database.MigrateAsync());

        Assert.Equal(2, await context.WatchProviderMedia.CountAsync());
        Assert.DoesNotContain("WatchProviderMedia_new", await TableNames(context));
        Assert.DoesNotContain(
            "20261103222056_FixWatchProviderUniqueness",
            await context.Database.GetAppliedMigrationsAsync()
        );
    }

    private static async Task<List<string>> TableNames(MediaContext context)
    {
        await using System.Data.Common.DbCommand command = context
            .Database.GetDbConnection()
            .CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        List<string> names = [];
        await using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<List<string>> IndexNames(MediaContext context)
    {
        await using System.Data.Common.DbCommand command = context
            .Database.GetDbConnection()
            .CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'WatchProviderMedia' AND name LIKE 'IX_%'";
        List<string> names = [];
        await using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
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
