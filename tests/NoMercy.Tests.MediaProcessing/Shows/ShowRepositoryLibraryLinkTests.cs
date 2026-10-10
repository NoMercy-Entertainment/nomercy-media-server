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
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Shows;

namespace NoMercy.Tests.MediaProcessing.Shows;

// A show belongs to exactly one library - Tv.LibraryId is a single FK - so
// the LibraryTv join table must never be allowed to disagree with it. This
// reproduces the measured bug: 388 shows on the Phoenix server carry TWO
// LibraryTv rows (their real library plus a stale one) because re-importing
// a show under a different library used to INSERT a second link instead of
// moving it.
[Trait("Category", "Unit")]
public class ShowRepositoryLibraryLinkTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public ShowRepositoryLibraryLinkTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task LinkToLibrary_MovingToAnotherLibrary_LeavesExactlyOneRow()
    {
        Library animeLibrary = new()
        {
            Id = Ulid.NewUlid(),
            Type = "anime",
            Title = "Anime",
        };
        Library tvLibrary = new()
        {
            Id = Ulid.NewUlid(),
            Type = "tv",
            Title = "TV Shows",
        };
        Tv show = new()
        {
            Id = 1,
            Title = "Naruto Shippuden",
            TitleSort = "Naruto Shippuden",
            LibraryId = tvLibrary.Id,
        };

        await using MediaContext seedCtx = new(_options);
        seedCtx.Libraries.AddRange(animeLibrary, tvLibrary);
        seedCtx.Tvs.Add(show);
        await seedCtx.SaveChangesAsync();

        await using MediaContext ctx1 = new(_options);
        ShowRepository repository1 = new(ctx1);
        await repository1.LinkToLibrary(animeLibrary, show);

        await using MediaContext ctx2 = new(_options);
        ShowRepository repository2 = new(ctx2);
        await repository2.LinkToLibrary(tvLibrary, show);

        await using MediaContext readCtx = new(_options);
        List<LibraryTv> links = await readCtx
            .LibraryTv.Where(lt => lt.TvId == show.Id)
            .ToListAsync();

        links.Should().HaveCount(1);
        links[0].LibraryId.Should().Be(tvLibrary.Id);
    }

    [Fact]
    public async Task LinkToLibrary_SameLibraryAgain_LeavesRowUntouchedAndPreservesAddedBy()
    {
        Library tvLibrary = new()
        {
            Id = Ulid.NewUlid(),
            Type = "tv",
            Title = "TV Shows",
        };
        Tv show = new()
        {
            Id = 2,
            Title = "Some Show",
            TitleSort = "Some Show",
            LibraryId = tvLibrary.Id,
        };

        await using MediaContext seedCtx = new(_options);
        seedCtx.Libraries.Add(tvLibrary);
        seedCtx.Tvs.Add(show);
        await seedCtx.SaveChangesAsync();

        await using MediaContext ctx1 = new(_options);
        ShowRepository repository1 = new(ctx1);
        await repository1.LinkToLibrary(tvLibrary, show, addedBy: LibraryLinkOrigin.Manual);

        // Re-link via a scan, which passes no addedBy (defaults to "file").
        // The existing row's origin must survive - a scan must not quietly
        // downgrade a link the owner added on purpose.
        await using MediaContext ctx2 = new(_options);
        ShowRepository repository2 = new(ctx2);
        await repository2.LinkToLibrary(tvLibrary, show);

        await using MediaContext readCtx = new(_options);
        List<LibraryTv> links = await readCtx
            .LibraryTv.Where(lt => lt.TvId == show.Id)
            .ToListAsync();

        links.Should().HaveCount(1);
        links[0].LibraryId.Should().Be(tvLibrary.Id);
        links[0].AddedBy.Should().Be(LibraryLinkOrigin.Manual);
    }
}
