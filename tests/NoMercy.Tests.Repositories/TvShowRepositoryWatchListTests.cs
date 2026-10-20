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
/// POST tv/{id}/watch-list is backed by TvShowRepository.AddToWatchListAsync.
/// The seed has one UserData row for show 1399: season 1 episode 1, Time 2400.
/// A watch-list entry is that first-episode row; "on the list" means the row
/// exists and is not hidden from continue watching.
/// </summary>
[Trait("Category", "Characterization")]
public class TvShowRepositoryWatchListTests : IDisposable
{
    private const int ShowId = 1399;
    private static readonly Ulid SeededFirstEpisodeRowId = Ulid.Parse("01JBBC0000000000000000TVSH");

    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly TvShowRepository _repository;

    public TvShowRepositoryWatchListTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateSeededFactory();
        _repository = new(_factory, new StubMediaTypeClassifier());
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<List<UserData>> ShowRowsAsync()
    {
        await using MediaContext context = await _factory.CreateDbContextAsync();
        return await context
            .UserData.AsNoTracking()
            .Where(ud => ud.UserId == SeedConstants.UserId && ud.TvId == ShowId)
            .ToListAsync();
    }

    private async Task HideSeededFirstEpisodeRowAsync()
    {
        await using MediaContext context = await _factory.CreateDbContextAsync();
        await context
            .UserData.Where(ud => ud.Id == SeededFirstEpisodeRowId)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(ud => ud.RemovedFromContinueWatching, true)
            );
    }

    // Finishing or hiding S1E1 sets RemovedFromContinueWatching on its row. Adding
    // the show back must make it visible again — and keep the position it had.
    [Fact]
    public async Task AddToWatchList_Add_UnhidesTheShow_WhenItsFirstEpisodeRowWasHidden()
    {
        await HideSeededFirstEpisodeRowAsync();

        bool result = await _repository.AddToWatchListAsync(
            ShowId,
            SeedConstants.UserId,
            add: true
        );

        Assert.True(result);
        UserData row = Assert.Single(await ShowRowsAsync());
        Assert.Equal(SeededFirstEpisodeRowId, row.Id);
        Assert.False(row.RemovedFromContinueWatching);
        Assert.Equal(2400, row.Time);
    }

    [Fact]
    public async Task AddToWatchList_Add_CreatesTheFirstEpisodeRow_WhenTheShowHasNone()
    {
        await using (MediaContext context = await _factory.CreateDbContextAsync())
        {
            await context.UserData.Where(ud => ud.TvId == ShowId).ExecuteDeleteAsync();
        }

        bool result = await _repository.AddToWatchListAsync(
            ShowId,
            SeedConstants.UserId,
            add: true
        );

        Assert.True(result);
        UserData row = Assert.Single(await ShowRowsAsync());
        Assert.Equal(SeedConstants.TvVideoFile1Id, row.VideoFileId);
        Assert.Equal(0, row.Time);
        Assert.False(row.RemovedFromContinueWatching);
    }
}
