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
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Shows;
using Xunit;

namespace NoMercy.Tests.MediaProcessing.Shows;

/// <summary>
/// Pins the real bug found live on production: 124 of 922 movies (and the
/// equivalent Tv rows) had their CreatedAt silently reset to "now" on a
/// rescan that failed to find the folder, because AddAsync stamped
/// CreatedAt unconditionally on every upsert. These tests exercise the real
/// EF Core upsert against a real SQLite schema - a mock of MediaContext
/// would not catch a regression in the ExecuteUpdateAsync gating itself.
/// </summary>
public sealed class ShowRepositoryCreatedAtTests : IDisposable
{
    private static readonly DateTime OriginalCreatedAt = new(2019, 3, 4, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WrongFallbackCreatedAt = new(
        2026,
        9,
        10,
        0,
        0,
        0,
        DateTimeKind.Utc
    );
    private static readonly DateTime NewRealCreatedAt = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Library _library;

    public ShowRepositoryCreatedAtTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext seed = new(_options);
        seed.Database.EnsureCreated();

        _library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Anime",
            Type = "anime",
        };
        seed.Libraries.Add(_library);
        seed.Tvs.Add(
            new()
            {
                Id = 1,
                Title = "Naruto Shippuden",
                LibraryId = _library.Id,
            }
        );
        seed.SaveChanges();

        // CreatedAt is [DatabaseGenerated(Computed)] (backed by SQLite's
        // CURRENT_TIMESTAMP default), so a plain Add+SaveChanges above cannot
        // set it - the row was just born with "now". Stamp the fixture's
        // intended original date the same way production does: a bulk
        // ExecuteUpdate, which bypasses the Computed-column restriction.
        seed.Tvs.Where(t => t.Id == 1)
            .ExecuteUpdate(s => s.SetProperty(t => t.CreatedAt, OriginalCreatedAt));
    }

    public void Dispose() => _connection.Dispose();

    // The exact regression: a rescan that could not find the folder this
    // pass (folderDateIsReal: false) must leave a previously-dated row's
    // CreatedAt untouched, even though the Tv object it upserts carries a
    // different (wrong, "now") CreatedAt value.
    [Fact]
    public async Task AddAsync_ExistingRow_FolderDateNotReal_KeepsOriginalCreatedAt()
    {
        await using MediaContext context = new(_options);
        ShowRepository repository = new(context);

        Tv rescanned = new()
        {
            Id = 1,
            Title = "Naruto Shippuden",
            LibraryId = _library.Id,
            CreatedAt = WrongFallbackCreatedAt,
        };

        await repository.AddAsync(rescanned, folderDateIsReal: false);

        await using MediaContext verify = new(_options);
        Tv? stored = await verify.Tvs.FindAsync(1);
        stored.Should().NotBeNull();
        stored!.CreatedAt.Should().Be(OriginalCreatedAt);
    }

    // The counterpart: when the folder genuinely was found this pass, the
    // real date DOES get written - the gate only blocks the "not found"
    // fallback, it does not freeze CreatedAt forever.
    [Fact]
    public async Task AddAsync_ExistingRow_FolderDateReal_UpdatesCreatedAt()
    {
        await using MediaContext context = new(_options);
        ShowRepository repository = new(context);

        Tv rescanned = new()
        {
            Id = 1,
            Title = "Naruto Shippuden",
            LibraryId = _library.Id,
            CreatedAt = NewRealCreatedAt,
        };

        await repository.AddAsync(rescanned, folderDateIsReal: true);

        await using MediaContext verify = new(_options);
        Tv? stored = await verify.Tvs.FindAsync(1);
        stored.Should().NotBeNull();
        stored!.CreatedAt.Should().Be(NewRealCreatedAt);
    }

    // A brand new row has no prior value to protect. CreatedAt is a
    // database-computed column (SQLite CURRENT_TIMESTAMP default) - neither
    // the initial Upsert insert nor a skipped ExecuteUpdateAsync ever writes
    // the C# object's CreatedAt for a fresh row, so it correctly reads as
    // "right now" rather than DateTime.MinValue or the caller's stale guess.
    [Fact]
    public async Task AddAsync_NewRow_FolderDateNotReal_GetsDatabaseDefaultNotTheStaleGuess()
    {
        await using MediaContext context = new(_options);
        ShowRepository repository = new(context);

        Tv newShow = new()
        {
            Id = 2,
            Title = "Rin: Daughters of Mnemosyne",
            LibraryId = _library.Id,
            CreatedAt = WrongFallbackCreatedAt,
        };

        await repository.AddAsync(newShow, folderDateIsReal: false);

        await using MediaContext verify = new(_options);
        Tv? stored = await verify.Tvs.FindAsync(2);
        stored.Should().NotBeNull();
        stored!.CreatedAt.Should().NotBe(WrongFallbackCreatedAt);
        // SQLite's CURRENT_TIMESTAMP has second precision, so it can read up
        // to ~1s "before" a sub-second .NET checkpoint through truncation
        // alone - a few seconds of slack proves "now", not an exact instant.
        stored.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }
}
