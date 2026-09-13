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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

// The hub, the playback timer and nothing else write watch progress. The hub's copy
// had no anime case and threw on every anime report, so the one shared upsert has to
// key anime on the show like tv, and skip anything it cannot store.
[Trait("Category", "Unit")]
public class UserDataRepositoryWatchProgressTests : IDisposable
{
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly UserDataRepository _repository;

    public UserDataRepositoryWatchProgressTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateSeededFactory();
        _repository = new(_factory);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static WatchProgress Progress(
        string playlistType,
        Ulid videoFileId,
        int time,
        string playlistId = "1399"
    ) =>
        new(
            SeedConstants.UserId,
            playlistType,
            playlistId,
            1399,
            videoFileId,
            time,
            "eng",
            "eng",
            "full"
        );

    private async Task<List<UserData>> RowsForFileAsync(Ulid videoFileId)
    {
        await using MediaContext ctx = _factory.CreateDbContext();
        return await ctx
            .UserData.AsNoTracking()
            .Where(row => row.UserId == SeedConstants.UserId && row.VideoFileId == videoFileId)
            .ToListAsync();
    }

    [Fact]
    public async Task UpsertWatchProgress_Anime_KeysOnTheShowAndUpdatesTheSameRow()
    {
        bool first = await _repository.UpsertWatchProgressAsync(
            Progress(MediaTypes.AnimeMediaType, SeedConstants.TvVideoFile2Id, 30)
        );
        bool second = await _repository.UpsertWatchProgressAsync(
            Progress(MediaTypes.AnimeMediaType, SeedConstants.TvVideoFile2Id, 90)
        );

        List<UserData> rows = await RowsForFileAsync(SeedConstants.TvVideoFile2Id);

        first.Should().BeTrue();
        second.Should().BeTrue();
        rows.Should().ContainSingle();
        rows[0].TvId.Should().Be(1399);
        rows[0].Time.Should().Be(90);
        rows[0].Type.Should().Be(MediaTypes.AnimeMediaType);
    }

    [Fact]
    public async Task UpsertWatchProgress_UnknownVideoFile_StoresNothing()
    {
        Ulid unknownFile = Ulid.NewUlid();

        bool stored = await _repository.UpsertWatchProgressAsync(
            Progress(MediaTypes.TvMediaType, unknownFile, 30)
        );

        stored.Should().BeFalse();
        (await RowsForFileAsync(unknownFile)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertWatchProgress_CollectionIdThatIsNotANumber_StoresNothing()
    {
        bool stored = await _repository.UpsertWatchProgressAsync(
            Progress(
                MediaTypes.CollectionMediaType,
                SeedConstants.MovieVideoFile2Id,
                30,
                "not-a-number"
            )
        );

        stored.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertWatchProgress_UnsupportedType_StoresNothing()
    {
        bool stored = await _repository.UpsertWatchProgressAsync(
            Progress("music", SeedConstants.TvVideoFile2Id, 30)
        );

        stored.Should().BeFalse();
    }
}
