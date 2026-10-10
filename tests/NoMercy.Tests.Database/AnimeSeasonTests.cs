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
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Tests.Database;

public class AnimeSeasonTests : IDisposable
{
    private readonly MediaContext _context;

    public AnimeSeasonTests()
    {
        DbContextOptionsBuilder<MediaContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");

        _context = new(optionsBuilder.Options);
        _context.Database.OpenConnection();
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Database.CloseConnection();
        _context.Dispose();
    }

    [Fact]
    public async Task AnimeSeason_LinkedToTv_PersistsAndRoundTrips()
    {
        MediaContext context = _context;

        Ulid libraryId = Ulid.NewUlid();
        AnimeSeason season = new()
        {
            Id = 1,
            Year = 2020,
            Quarter = "SUMMER",
        };
        context.AnimeSeasons.Add(season);
        context.Libraries.Add(new Library { Id = libraryId, Title = "Anime" });
        context.Tvs.Add(
            new Tv
            {
                Id = 42,
                Title = "Re:Zero",
                LibraryId = libraryId,
            }
        );
        await context.SaveChangesAsync();

        context.AnimeSeasonTv.Add(new AnimeSeasonTv { AnimeSeasonId = 1, TvId = 42 });
        await context.SaveChangesAsync();

        List<AnimeSeasonTv> links = await context.AnimeSeasonTv.AsNoTracking().ToListAsync();

        links.Should().ContainSingle(l => l.AnimeSeasonId == 1 && l.TvId == 42);
    }
}
