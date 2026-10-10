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

public class AnimeThemeTests : IDisposable
{
    private readonly MediaContext _context;

    public AnimeThemeTests()
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
    public async Task AnimeTheme_LinkedToTv_PersistsAndRoundTrips()
    {
        MediaContext context = _context;

        Ulid libraryId = Ulid.NewUlid();
        AnimeTheme theme = new() { Id = 1, Name = "Isekai" };
        context.AnimeThemes.Add(theme);
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

        context.AnimeThemeTv.Add(new AnimeThemeTv { AnimeThemeId = 1, TvId = 42 });
        await context.SaveChangesAsync();

        List<AnimeThemeTv> links = await context.AnimeThemeTv.AsNoTracking().ToListAsync();

        links.Should().ContainSingle(l => l.AnimeThemeId == 1 && l.TvId == 42);
    }
}
