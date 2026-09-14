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
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Tests.Repositories.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Unit")]
public class SetupQueriesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<MediaContext> _factory;

    public SetupQueriesTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateFactory();

        using MediaContext seedContext = _factory.CreateDbContext();
        TestMediaContextFactory.SeedData(seedContext);
    }

    [Fact]
    public async Task GetSetupLibrariesAsync_ReturnsOnlyTheUsersLibrariesInOrder()
    {
        LibraryRepository repository = new(_factory);

        List<Library> mine = await repository.GetSetupLibrariesAsync(SeedConstants.UserId);
        List<Library> stranger = await repository.GetSetupLibrariesAsync(Guid.NewGuid());

        mine.Should().Contain(library => library.Id == SeedConstants.MovieLibraryId);
        mine.Select(library => library.Order).Should().BeInAscendingOrder();
        stranger.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserPlaylistsAsync_ReturnsOnlyTheUsersPlaylists()
    {
        await using (MediaContext context = await _factory.CreateDbContextAsync())
        {
            context.Playlists.Add(
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Mine",
                    UserId = SeedConstants.UserId,
                }
            );
            await context.SaveChangesAsync();
        }

        MusicRepository repository = new(_factory);

        List<Playlist> mine = await repository.GetUserPlaylistsAsync(SeedConstants.UserId);
        List<Playlist> stranger = await repository.GetUserPlaylistsAsync(Guid.NewGuid());

        mine.Select(playlist => playlist.Name).Should().Contain("Mine");
        stranger.Should().BeEmpty();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
