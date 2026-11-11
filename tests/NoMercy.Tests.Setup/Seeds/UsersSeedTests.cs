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
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Information;
using NoMercy.Service.Seeds;
using NoMercy.Service.Seeds.Dto;
using NoMercy.Storage;

namespace NoMercy.Tests.Setup.Seeds;

/// <summary>
/// Authenticated startup syncs server users and grants seed libraries only to
/// newly inserted users. Existing library choices remain authoritative.
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
    private static readonly Ulid LibraryId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV");

    private static Guid UserId(int number) =>
        Guid.Parse($"00000000-0000-4000-8000-{number:000000000000}");

    private static Mock<IStorage> LibrariesStorage()
    {
        Mock<IStorage> storage = new();
        storage.Setup(s => s.Exists(AppFiles.LibrariesSeedFile)).Returns(true);
        storage
            .Setup(s =>
                s.ReadAllTextAsync(AppFiles.LibrariesSeedFile, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync($"[{{\"id\":\"{LibraryId}\",\"title\":\"Test\",\"type\":\"movie\"}}]");
        return storage;
    }

    [Fact]
    public async Task Init_150UpstreamUsers_GrantsSeedLibraryToAll50NewUsers()
    {
        await using MediaContext seedCtx = CreateContext();
        seedCtx.Libraries.Add(
            new Library
            {
                Id = LibraryId,
                Title = "Test",
                Type = "movie",
            }
        );

        for (int number = 1; number <= 100; number++)
        {
            Guid id = UserId(number);
            seedCtx.Users.Add(
                new User
                {
                    Id = id,
                    Email = $"user{number}@example.com",
                    Name = $"User {number}",
                    Allowed = true,
                    Owner = number == 1,
                }
            );
            seedCtx.LibraryUser.Add(new LibraryUser(LibraryId, id));
        }

        await seedCtx.SaveChangesAsync();

        ServerUserDtoData[] upstreamUsers = Enumerable
            .Range(1, 150)
            .Select(number => new ServerUserDtoData
            {
                UserId = UserId(number).ToString(),
                Email = $"user{number}@example.com",
                Name = $"User {number}",
                IsOwner = number == 1,
                Enabled = true,
            })
            .ToArray();
        FakeServerUserApiClient api = new(upstreamUsers);
        ServerUserSyncService syncService = new(api);
        Mock<IStorage> storage = LibrariesStorage();

        await using MediaContext runCtx = CreateContext();
        await runCtx.Init(storage.Object, "valid-token", syncService);

        await using MediaContext assertCtx = CreateContext();
        Assert.Equal(150, await assertCtx.LibraryUser.CountAsync(lu => lu.LibraryId == LibraryId));
    }

    [Fact]
    public async Task Init_RepairsNewUserAccess_LeavesExistingUserWithNoLibrariesAlone()
    {
        await using MediaContext seedCtx = CreateContext();
        seedCtx.Libraries.Add(
            new Library
            {
                Id = LibraryId,
                Title = "Test",
                Type = "movie",
            }
        );
        seedCtx.Users.AddRange(
            new User
            {
                Id = UserId(1),
                Email = "user1@example.com",
                Name = "Owner",
                Allowed = true,
                Owner = true,
            },
            new User
            {
                Id = UserId(2),
                Email = "user2@example.com",
                Name = "Existing user",
                Allowed = true,
            }
        );
        seedCtx.LibraryUser.Add(new LibraryUser(LibraryId, UserId(1)));
        await seedCtx.SaveChangesAsync();

        FakeServerUserApiClient api = new([
            new()
            {
                UserId = UserId(1).ToString(),
                Email = "user1@example.com",
                Name = "Owner",
                IsOwner = true,
            },
            new()
            {
                UserId = UserId(2).ToString(),
                Email = "user2@example.com",
                Name = "Existing user",
            },
            new()
            {
                UserId = UserId(3).ToString(),
                Email = "user3@example.com",
                Name = "New user",
            },
        ]);
        ServerUserSyncService syncService = new(api);
        Mock<IStorage> storage = LibrariesStorage();

        await using MediaContext runCtx = CreateContext();
        await runCtx.Init(storage.Object, "valid-token", syncService);

        await using MediaContext assertCtx = CreateContext();
        Assert.Equal(1, await assertCtx.LibraryUser.CountAsync(lu => lu.UserId == UserId(1)));
        Assert.Equal(0, await assertCtx.LibraryUser.CountAsync(lu => lu.UserId == UserId(2)));
        Assert.Equal(1, await assertCtx.LibraryUser.CountAsync(lu => lu.UserId == UserId(3)));
    }

    [Fact]
    public async Task Init_RunningTwice_DoesNotAddLibraryAccessAgain()
    {
        await using MediaContext seedCtx = CreateContext();
        seedCtx.Libraries.Add(
            new Library
            {
                Id = LibraryId,
                Title = "Test",
                Type = "movie",
            }
        );
        await seedCtx.SaveChangesAsync();

        FakeServerUserApiClient api = new([
            new()
            {
                UserId = UserId(1).ToString(),
                Email = "user1@example.com",
                Name = "Owner",
                IsOwner = true,
            },
        ]);
        ServerUserSyncService syncService = new(api);
        Mock<IStorage> storage = LibrariesStorage();

        await using (MediaContext firstRun = CreateContext())
            await firstRun.Init(storage.Object, "valid-token", syncService);

        await using (MediaContext secondRun = CreateContext())
            await secondRun.Init(storage.Object, "valid-token", syncService);

        await using MediaContext assertCtx = CreateContext();
        Assert.Equal(1, await assertCtx.LibraryUser.CountAsync());
        Assert.Equal(1, await assertCtx.Users.CountAsync());
        storage.Verify(
            s => s.ReadAllTextAsync(AppFiles.LibrariesSeedFile, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

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
    public async Task Init_NonEmptyUsersTable_CallsSyncService()
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

        Assert.True(api.WasCalled);
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
