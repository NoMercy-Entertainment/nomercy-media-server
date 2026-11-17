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
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Tests.Database.Migrations;

/// <summary>
/// Special backdrops from older builds carry the retired storage.nomercy.tv
/// host, which the special DTOs used to strip on every read (#113). After the
/// upgrade every stored backdrop must be exactly what that read-time strip
/// produced, so clients see no change, and nothing else may be touched.
/// </summary>
[Trait("Category", "Integration")]
public class StaleStorageHostMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261116185941_RemovePhantomLibraryTvLinks";
    private const string StaleHost = "https://storage.nomercy.tv/laravel";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"nm_stale_storage_host_migration_{Guid.NewGuid():N}.db"
    );

    public void Dispose()
    {
        // Pooled handles keep the file locked on Windows past Dispose().
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("https://storage.nomercy.tv/laravel/clje9xd4v0000d4ef0usufhy9.jpg")]
    [InlineData("/clje9xd4v0000d4ef0usufhy9.jpg")]
    [InlineData("https://storage.nomercy.tv/laravel")]
    [InlineData("HTTPS://STORAGE.NOMERCY.TV/laravel/upper.jpg")]
    [InlineData("https://image.tmdb.org/t/p/original/other.jpg")]
    [InlineData(null)]
    public async Task StoredBackdrop_BecomesWhatTheReadTimeStripReturned(string? stored)
    {
        Ulid specialId = Ulid.NewUlid();
        string poster = $"{StaleHost}/poster.jpg";

        await using (MediaContext oldContext = OpenContext())
        {
            await oldContext.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            oldContext.Add(
                new Special
                {
                    Id = specialId,
                    Title = "Marvel Cinematic Universe",
                    Backdrop = stored,
                    Poster = poster,
                }
            );
            await oldContext.SaveChangesAsync();
        }

        await using (MediaContext upgradeContext = OpenContext())
        {
            await upgradeContext.Database.MigrateAsync();
        }

        await using MediaContext readContext = OpenContext();
        Special special = await readContext
            .Specials.AsNoTracking()
            .SingleAsync(row => row.Id == specialId);

        Assert.Equal(stored?.Replace(StaleHost, ""), special.Backdrop);
        // The DTOs never stripped the poster, so neither does the migration.
        Assert.Equal(poster, special.Poster);
    }

    private MediaContext OpenContext()
    {
        DbContextOptionsBuilder<MediaContext> builder = new();
        builder.UseSqlite($"Data Source={_databasePath}");

        return new(builder.Options);
    }
}
