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

public class AnimeDemographicTests : IDisposable
{
    private readonly MediaContext _context;

    public AnimeDemographicTests()
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
    public async Task AnimeDemographic_LinkedToTv_PersistsAndRoundTrips()
    {
        MediaContext context = _context;

        Ulid libraryId = Ulid.NewUlid();
        AnimeDemographic demographic = new() { Id = 1, Name = "Shounen" };
        context.AnimeDemographics.Add(demographic);
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

        context.AnimeDemographicTv.Add(
            new AnimeDemographicTv { AnimeDemographicId = 1, TvId = 42 }
        );
        await context.SaveChangesAsync();

        List<AnimeDemographicTv> links = await context
            .AnimeDemographicTv.AsNoTracking()
            .ToListAsync();

        links.Should().ContainSingle(l => l.AnimeDemographicId == 1 && l.TvId == 42);
    }
}
