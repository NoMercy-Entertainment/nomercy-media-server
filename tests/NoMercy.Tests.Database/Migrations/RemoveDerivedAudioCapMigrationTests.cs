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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NoMercy.Database;
using NoMercy.Database.Models.Common;

namespace NoMercy.Tests.Database.Migrations;

/// <summary>
/// The size cap of the derived-audio store was a setting an owner could have
/// saved (#689). The store is gone, so the update takes the saved value with
/// it and leaves every other setting alone.
/// </summary>
[Trait("Category", "Integration")]
public class RemoveDerivedAudioCapMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260411165439_InitialAppSchema";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"nm_remove_derived_audio_cap_migration_{Guid.NewGuid():N}.db"
    );

    public void Dispose()
    {
        // Pooled handles keep the file locked on Windows past Dispose().
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
            File.Delete(_databasePath);

        GC.SuppressFinalize(this);
    }

    private AppDbContext OpenContext()
    {
        DbContextOptionsBuilder<AppDbContext> builder = new();
        builder.UseSqlite($"Data Source={_databasePath}");

        return new(builder.Options);
    }

    [Fact]
    public async Task TheSavedCap_IsRemoved_AndEveryOtherSettingStays()
    {
        await using (AppDbContext oldContext = OpenContext())
        {
            await oldContext.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            oldContext.Configuration.AddRange(
                new Configuration { Key = "derivedAudioCapGb", Value = "40" },
                new Configuration { Key = "queueWorkers", Value = "3" }
            );
            await oldContext.SaveChangesAsync();
        }

        await using (AppDbContext upgradeContext = OpenContext())
        {
            await upgradeContext.Database.MigrateAsync();
        }

        await using AppDbContext readContext = OpenContext();

        Configuration kept = await readContext.Configuration.AsNoTracking().SingleAsync();
        kept.Key.Should().Be("queueWorkers");
        kept.Value.Should().Be("3");
    }
}
