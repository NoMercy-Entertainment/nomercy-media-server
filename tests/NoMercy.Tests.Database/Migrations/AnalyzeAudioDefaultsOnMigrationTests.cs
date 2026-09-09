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

namespace NoMercy.Tests.Database.Migrations;

/// <summary>
/// Audio analysis being on by default is only half a promise if it reaches
/// nobody who already installed the server. This replays the chain against a
/// real file the way a self-hosted upgrade does: a database that predates the
/// change, carrying libraries that were created with the column off, then the
/// new migration on top of it.
/// </summary>
public class AnalyzeAudioDefaultsOnMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260905191847_TrackAudioAnalysis";

    private readonly string _databasePath;

    public AnalyzeAudioDefaultsOnMigrationTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"nm_analyzeaudio_migration_{Guid.NewGuid():N}.db"
        );
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools physical file handles across connections
        // even after Dispose(); on Windows the OS-level lock outlives the
        // `using` above, so the delete fails unless the pool is cleared first.
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    private MediaContext OpenContext()
    {
        DbContextOptionsBuilder<MediaContext> builder = new();
        builder.UseSqlite($"Data Source={_databasePath}");

        return new(builder.Options);
    }

    private static Library MusicLibrary(bool? analyzeAudio)
    {
        Library library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "A Library",
            Type = "music",
        };

        if (analyzeAudio is not null)
        {
            library.AnalyzeAudio = analyzeAudio.Value;
        }

        return library;
    }

    /// <summary>
    /// The store default is on, and EF must still write an explicit "off" for
    /// a library that opted out — a default that swallows the opt-out would
    /// analyse a library its owner said no to.
    /// </summary>
    [Fact]
    public async Task ALibraryThatOptedOut_StaysOffAcrossASaveAndReload()
    {
        Ulid libraryId;

        await using (MediaContext seedContext = OpenContext())
        {
            await seedContext.Database.MigrateAsync();

            Library library = MusicLibrary(analyzeAudio: false);
            libraryId = library.Id;

            seedContext.Libraries.Add(library);
            await seedContext.SaveChangesAsync();
        }

        await using MediaContext readContext = OpenContext();
        Library reloaded = await readContext
            .Libraries.AsNoTracking()
            .SingleAsync(library => library.Id == libraryId);

        Assert.False(reloaded.AnalyzeAudio);
    }

    [Fact]
    public async Task ALibraryThatSaysNothing_IsStoredWithAnalysisOn()
    {
        Ulid libraryId;

        await using (MediaContext seedContext = OpenContext())
        {
            await seedContext.Database.MigrateAsync();

            Library library = MusicLibrary(analyzeAudio: null);
            libraryId = library.Id;

            seedContext.Libraries.Add(library);
            await seedContext.SaveChangesAsync();
        }

        await using MediaContext readContext = OpenContext();
        Library reloaded = await readContext
            .Libraries.AsNoTracking()
            .SingleAsync(library => library.Id == libraryId);

        Assert.True(reloaded.AnalyzeAudio);
    }

    /// <summary>
    /// The upgrade path: every music library that already existed when the
    /// column could only be off is turned on, and nothing else is touched.
    /// </summary>
    [Fact]
    public async Task ExistingMusicLibraries_AreTurnedOnByTheMigration()
    {
        Ulid musicLibraryId = Ulid.NewUlid();
        Ulid movieLibraryId = Ulid.NewUlid();

        await using (MediaContext oldContext = OpenContext())
        {
            IMigrator migrator = oldContext.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            oldContext.Libraries.AddRange(
                new Library
                {
                    Id = musicLibraryId,
                    Title = "A Library",
                    Type = "music",
                    AnalyzeAudio = false,
                },
                new Library
                {
                    Id = movieLibraryId,
                    Title = "Another Library",
                    Type = "movie",
                    AnalyzeAudio = false,
                }
            );

            await oldContext.SaveChangesAsync();
        }

        await using (MediaContext upgradeContext = OpenContext())
        {
            await upgradeContext.Database.MigrateAsync();
        }

        await using MediaContext readContext = OpenContext();
        List<Library> libraries = await readContext.Libraries.AsNoTracking().ToListAsync();

        Assert.True(libraries.Single(library => library.Id == musicLibraryId).AnalyzeAudio);
        Assert.False(libraries.Single(library => library.Id == movieLibraryId).AnalyzeAudio);
    }
}
