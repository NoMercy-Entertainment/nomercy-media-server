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
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Tests.Repositories.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Unit")]
public class UserCacheRefreshTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<MediaContext> _factory;

    public UserCacheRefreshTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateFactory();

        using MediaContext seedContext = _factory.CreateDbContext();
        TestMediaContextFactory.SeedData(seedContext);
    }

    [Fact]
    public async Task RefreshThroughAFactory_LoadsUsersAndFolderIds()
    {
        UserCache cache = new();

        await cache.RefreshUsersAsync(_factory);
        await cache.RefreshFolderIdsAsync(_factory);

        cache.GetUser(SeedConstants.UserId).Should().NotBeNull();
        cache.FolderIds.Should().Contain(SeedConstants.MovieFolderId);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
