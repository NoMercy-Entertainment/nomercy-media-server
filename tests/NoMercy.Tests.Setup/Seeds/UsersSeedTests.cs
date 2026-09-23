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
using NoMercy.Database.Models.Users;
using NoMercy.Service.Seeds;
using NoMercy.Service.Seeds.Dto;
using NoMercy.Storage;

namespace NoMercy.Tests.Setup.Seeds;

/// <summary>
/// <see cref="UsersSeed"/> is now a thin first-boot wrapper around
/// <see cref="ServerUserSyncService"/> — these tests confirm boot-time
/// behavior is unchanged: it still seeds an empty Users table, and still
/// never re-runs once any user exists. Ongoing sync after first boot is
/// nomercy-tv's PushServerUserJob, not a recurring poll on this side.
/// </summary>
[Trait("Category", "Unit")]
public class UsersSeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private static readonly Mock<IStorage> NoLibrariesStorage = new();

    public UsersSeedTests()
    {
        _connection = new("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private MediaContext CreateContext() => new(_options);

    private static readonly Guid OwnerId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task Init_EmptyUsersTable_SeedsFromUpstream()
    {
        FakeServerUserApiClient api = new([
            new()
            {
                UserId = OwnerId.ToString(),
                Name = "Owner",
                Email = "owner@example.com",
                IsOwner = true,
                Enabled = true,
            },
        ]);
        ServerUserSyncService realSyncService = new(api);

        await using MediaContext ctx = CreateContext();
        await ctx.Init(NoLibrariesStorage.Object, "valid-token", realSyncService);

        User? owner = await ctx.Users.FirstOrDefaultAsync(u => u.Id == OwnerId);
        Assert.NotNull(owner);
        Assert.True(owner!.Owner);
        Assert.True(api.WasCalled);
    }

    [Fact]
    public async Task Init_NonEmptyUsersTable_NeverCallsSyncService()
    {
        await using MediaContext seedCtx = CreateContext();
        seedCtx.Users.Add(
            new()
            {
                Id = OwnerId,
                Email = "owner@example.com",
                Name = "Owner",
                Allowed = true,
                Owner = true,
            }
        );
        await seedCtx.SaveChangesAsync();

        FakeServerUserApiClient api = new([]);
        ServerUserSyncService realSyncService = new(api);

        await using MediaContext ctx = CreateContext();
        await ctx.Init(NoLibrariesStorage.Object, "valid-token", realSyncService);

        // The one-shot seed must stay gated — ongoing sync is the cron job's job.
        Assert.False(api.WasCalled);
    }

    [Fact]
    public async Task Init_NoAccessToken_DoesNotThrow()
    {
        await using MediaContext ctx = CreateContext();

        Exception? exception = await Record.ExceptionAsync(() =>
            UsersSeed.Init(ctx, NoLibrariesStorage.Object, null)
        );

        Assert.Null(exception);
    }
}
