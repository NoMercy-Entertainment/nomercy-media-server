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
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.MediaProcessing.Movies;
using Xunit;

namespace NoMercy.Tests.MediaProcessing.Movies;

/// <summary>
/// Pins the real bug found live on production: "Evil Dead" carried
/// CreatedAt 2026-09-05 in a 4-day-old backup but read as 2026-09-09 (today)
/// in the live database - 124 of 922 movies were affected the same way.
/// Add() stamped CreatedAt unconditionally on every upsert; these tests
/// exercise the real EF Core upsert against a real SQLite schema, since a
/// mock of MediaContext cannot catch a regression in the ExecuteUpdateAsync
/// gating itself.
/// </summary>
public sealed class MovieRepositoryCreatedAtTests : IDisposable
{
    private static readonly DateTime OriginalCreatedAt = new(
        2026,
        9,
        5,
        8,
        49,
        19,
        DateTimeKind.Utc
    );
    private static readonly DateTime WrongFallbackCreatedAt = new(
        2026,
        9,
        9,
        8,
        43,
        12,
        DateTimeKind.Utc
    );
    private static readonly DateTime NewRealCreatedAt = new(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Library _library;

    public MovieRepositoryCreatedAtTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext seed = new(_options);
        seed.Database.EnsureCreated();

        _library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Films",
            Type = "movie",
        };
        seed.Libraries.Add(_library);
        seed.Movies.Add(
            new()
            {
                Id = 1,
                Title = "Evil Dead",
                LibraryId = _library.Id,
            }
        );
        seed.SaveChanges();

        // CreatedAt is [DatabaseGenerated(Computed)] (backed by SQLite's
        // CURRENT_TIMESTAMP default), so a plain Add+SaveChanges above cannot
        // set it - the row was just born with "now". Stamp the fixture's
        // intended original date the same way production does: a bulk
        // ExecuteUpdate, which bypasses the Computed-column restriction.
        seed.Movies.Where(m => m.Id == 1)
            .ExecuteUpdate(s => s.SetProperty(t => t.CreatedAt, OriginalCreatedAt));
    }

    public void Dispose() => _connection.Dispose();

    // The exact regression: a rescan that could not find the folder this
    // pass (folderDateIsReal: false) must leave a previously-dated row's
    // CreatedAt untouched, even though the Movie object it upserts carries
    // a different (wrong, "now") CreatedAt value.
    [Fact]
    public async Task Add_ExistingRow_FolderDateNotReal_KeepsOriginalCreatedAt()
    {
        await using MediaContext context = new(_options);
        MovieRepository repository = new(context);

        Movie rescanned = new()
        {
            Id = 1,
            Title = "Evil Dead",
            LibraryId = _library.Id,
            CreatedAt = WrongFallbackCreatedAt,
        };

        await repository.Add(rescanned, folderDateIsReal: false);

        await using MediaContext verify = new(_options);
        Movie? stored = await verify.Movies.FindAsync(1);
        stored.Should().NotBeNull();
        stored!.CreatedAt.Should().Be(OriginalCreatedAt);
    }

    // The counterpart: when the folder genuinely was found this pass, the
    // real date DOES get written - the gate only blocks the "not found"
    // fallback, it does not freeze CreatedAt forever.
    [Fact]
    public async Task Add_ExistingRow_FolderDateReal_UpdatesCreatedAt()
    {
        await using MediaContext context = new(_options);
        MovieRepository repository = new(context);

        Movie rescanned = new()
        {
            Id = 1,
            Title = "Evil Dead",
            LibraryId = _library.Id,
            CreatedAt = NewRealCreatedAt,
        };

        await repository.Add(rescanned, folderDateIsReal: true);

        await using MediaContext verify = new(_options);
        Movie? stored = await verify.Movies.FindAsync(1);
        stored.Should().NotBeNull();
        stored!.CreatedAt.Should().Be(NewRealCreatedAt);
    }

    // A brand new row has no prior value to protect. CreatedAt is a
    // database-computed column (SQLite CURRENT_TIMESTAMP default) - neither
    // the initial Upsert insert nor a skipped ExecuteUpdateAsync ever writes
    // the C# object's CreatedAt for a fresh row, so it correctly reads as
    // "right now" rather than DateTime.MinValue or the caller's stale guess.
    [Fact]
    public async Task Add_NewRow_FolderDateNotReal_GetsDatabaseDefaultNotTheStaleGuess()
    {
        await using MediaContext context = new(_options);
        MovieRepository repository = new(context);

        Movie newMovie = new()
        {
            Id = 2,
            Title = "Shrek 5",
            LibraryId = _library.Id,
            CreatedAt = WrongFallbackCreatedAt,
        };

        await repository.Add(newMovie, folderDateIsReal: false);

        await using MediaContext verify = new(_options);
        Movie? stored = await verify.Movies.FindAsync(2);
        stored.Should().NotBeNull();
        stored!.CreatedAt.Should().NotBe(WrongFallbackCreatedAt);
        // SQLite's CURRENT_TIMESTAMP has second precision, so it can read up
        // to ~1s "before" a sub-second .NET checkpoint through truncation
        // alone - a few seconds of slack proves "now", not an exact instant.
        stored.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }
}
