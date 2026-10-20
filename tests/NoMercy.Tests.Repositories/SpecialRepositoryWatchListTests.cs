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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// POST specials/{id}/watch-list is backed by SpecialRepository.AddToWatchListAsync.
/// A special's watch-list entry is a UserData row keyed on
/// (VideoFileId, UserId, SpecialId), the key UpsertWatchProgressAsync uses for
/// specials. The seed already holds a standalone row for movie 129 (SpecialId null).
/// </summary>
[Trait("Category", "Characterization")]
public class SpecialRepositoryWatchListTests : IDisposable
{
    private const int MovieWithoutFilesId = 990_001;
    private static readonly Ulid SpecialId = Ulid.NewUlid();
    private static readonly Ulid SeededStandaloneMovieRowId = Ulid.Parse(
        "01JABC0000000000000000MOVI"
    );

    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;
    private readonly SpecialRepository _repository;

    public SpecialRepositoryWatchListTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateSeededFactory();
        _context = _factory.CreateDbContext();
        _repository = new(_context, _factory);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedSpecialAsync(params (int MovieId, int Order)[] items)
    {
        await using MediaContext context = await _factory.CreateDbContextAsync();
        if (items.Any(item => item.MovieId == MovieWithoutFilesId))
        {
            context.Movies.Add(
                new()
                {
                    Id = MovieWithoutFilesId,
                    Title = "Movie Without Files",
                    TitleSort = "movie without files",
                    LibraryId = SeedConstants.MovieLibraryId,
                }
            );
        }
        context.Specials.Add(new() { Id = SpecialId, Title = "Test Special" });
        await context.SaveChangesAsync();

        foreach ((int movieId, int order) in items)
        {
            context.SpecialItems.Add(
                new()
                {
                    SpecialId = SpecialId,
                    MovieId = movieId,
                    Order = order,
                }
            );
        }
        await context.SaveChangesAsync();
    }

    private async Task<List<UserData>> SpecialRowsAsync()
    {
        await using MediaContext context = await _factory.CreateDbContextAsync();
        return await context
            .UserData.AsNoTracking()
            .Where(ud => ud.UserId == SeedConstants.UserId && ud.SpecialId == SpecialId)
            .ToListAsync();
    }

    // The viewer watched movie 129 on its own, so a row for that file exists with
    // SpecialId null. It is not the special's entry and must not stop one from
    // being written.
    [Fact]
    public async Task AddToWatchList_Add_CreatesTheSpecialRow_WhenTheMovieWasWatchedStandalone()
    {
        await SeedSpecialAsync((129, 0));

        bool result = await _repository.AddToWatchListAsync(
            SpecialId,
            SeedConstants.UserId,
            add: true
        );

        Assert.True(result);
        UserData row = Assert.Single(await SpecialRowsAsync());
        Assert.Equal(SeedConstants.MovieVideoFile1Id, row.VideoFileId);
        Assert.Equal(0, row.Time);

        await using MediaContext context = await _factory.CreateDbContextAsync();
        UserData standalone = await context
            .UserData.AsNoTracking()
            .SingleAsync(ud => ud.Id == SeededStandaloneMovieRowId);
        Assert.Null(standalone.SpecialId);
        Assert.Equal(3600, standalone.Time);
    }

    [Fact]
    public async Task AddToWatchList_Add_UsesTheFirstItemThatHasAFile()
    {
        await SeedSpecialAsync((MovieWithoutFilesId, 0), (129, 1));

        bool result = await _repository.AddToWatchListAsync(
            SpecialId,
            SeedConstants.UserId,
            add: true
        );

        Assert.True(result);
        UserData row = Assert.Single(await SpecialRowsAsync());
        Assert.Equal(SeedConstants.MovieVideoFile1Id, row.VideoFileId);
    }

    [Fact]
    public async Task AddToWatchList_Add_ReturnsFalse_WhenNoItemHasAFile()
    {
        await SeedSpecialAsync((MovieWithoutFilesId, 0));

        bool result = await _repository.AddToWatchListAsync(
            SpecialId,
            SeedConstants.UserId,
            add: true
        );

        Assert.False(result);
        Assert.Empty(await SpecialRowsAsync());
    }

    [Fact]
    public async Task AddToWatchList_Add_Twice_LeavesOneRow()
    {
        await SeedSpecialAsync((129, 0));

        await _repository.AddToWatchListAsync(SpecialId, SeedConstants.UserId, add: true);
        bool result = await _repository.AddToWatchListAsync(
            SpecialId,
            SeedConstants.UserId,
            add: true
        );

        Assert.True(result);
        Assert.Single(await SpecialRowsAsync());
    }
}
