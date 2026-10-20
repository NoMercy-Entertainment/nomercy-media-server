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

    // Removing the show from the list used to delete every UserData row it had:
    // the resume point of each episode, with nothing to restore from. The rows
    // must stay, hidden, with their positions intact.
    [Fact]
    public async Task AddToWatchList_Remove_HidesTheShow_AndKeepsEveryEpisodesResumePoint()
    {
        await AddSecondEpisodeRowAsync();
        Dictionary<Ulid, int?> timesBefore = (await ShowRowsAsync()).ToDictionary(
            ud => ud.Id,
            ud => ud.Time
        );
        Assert.Equal(2, timesBefore.Count);

        bool result = await _repository.AddToWatchListAsync(
            ShowId,
            SeedConstants.UserId,
            add: false
        );

        Assert.True(result);
        List<UserData> after = await ShowRowsAsync();
        Assert.Equal(2, after.Count);
        foreach (UserData row in after)
        {
            Assert.True(row.RemovedFromContinueWatching);
            Assert.Equal(timesBefore[row.Id], row.Time);
        }
    }

    [Fact]
    public async Task AddToWatchList_Remove_LeavesOtherItemsListed()
    {
        await _repository.AddToWatchListAsync(ShowId, SeedConstants.UserId, add: false);

        await using MediaContext context = await _factory.CreateDbContextAsync();
        List<UserData> movieRows = await context
            .UserData.AsNoTracking()
            .Where(ud => ud.UserId == SeedConstants.UserId && ud.MovieId == 129)
            .ToListAsync();
        Assert.Equal(2, movieRows.Count);
        Assert.All(movieRows, row => Assert.False(row.RemovedFromContinueWatching));
    }

    [Fact]
    public async Task AddToWatchList_RemoveThenAdd_ListsTheShowAgain_WithItsPositions()
    {
        await AddSecondEpisodeRowAsync();

        await _repository.AddToWatchListAsync(ShowId, SeedConstants.UserId, add: false);
        await _repository.AddToWatchListAsync(ShowId, SeedConstants.UserId, add: true);

        List<UserData> rows = await ShowRowsAsync();
        Assert.Equal(2, rows.Count);
        UserData firstEpisode = Assert.Single(rows, ud => ud.Id == SeededFirstEpisodeRowId);
        Assert.False(firstEpisode.RemovedFromContinueWatching);
        Assert.Equal(2400, firstEpisode.Time);
        UserData secondEpisode = Assert.Single(
            rows,
            ud => ud.VideoFileId == SeedConstants.TvVideoFile2Id
        );
        Assert.Equal(1200, secondEpisode.Time);
    }

    private async Task AddSecondEpisodeRowAsync()
    {
        await using MediaContext context = await _factory.CreateDbContextAsync();
        context.UserData.Add(
            new()
            {
                UserId = SeedConstants.UserId,
                TvId = ShowId,
                VideoFileId = SeedConstants.TvVideoFile2Id,
                Type = "tv",
                Time = 1200,
                LastPlayedDate = "2026-02-03T14:00:00Z",
            }
        );
        await context.SaveChangesAsync();
    }
}
