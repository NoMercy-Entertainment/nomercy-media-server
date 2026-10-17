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
using NoMercy.Storage;

namespace NoMercy.Tests.Setup.Seeds;

/// <summary>
/// <see cref="UsersSeed"/> syncs users on first boot and repairs missing
/// default-library grants on later boots. Ongoing user sync after first boot
/// is nomercy-tv's PushServerUserJob, not a recurring poll on this side.
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

        // The one-shot user sync stays gated; the library repair does not call it.
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

    [Fact]
    public async Task Init_RepairsLibraryAccessBeyondFirstHundredUsers()
    {
        Ulid libraryId = Ulid.NewUlid();
        await using (MediaContext seedCtx = CreateContext())
        {
            seedCtx.Libraries.Add(
                new()
                {
                    Id = libraryId,
                    Title = "Movies",
                    Type = "movie",
                }
            );
            for (int index = 1; index <= 150; index++)
            {
                Guid userId = Guid.Parse($"00000000-0000-4000-8000-{index:000000000000}");
                seedCtx.Users.Add(
                    new()
                    {
                        Id = userId,
                        Email = $"user{index}@example.test",
                        Name = $"User {index}",
                        Allowed = true,
                    }
                );
                if (index <= 100)
                    seedCtx.LibraryUser.Add(new(libraryId, userId));
            }
            await seedCtx.SaveChangesAsync();
        }

        Mock<IStorage> storage = new();
        storage.Setup(s => s.Exists(AppFiles.LibrariesSeedFile)).Returns(true);
        storage
            .Setup(s =>
                s.ReadAllTextAsync(AppFiles.LibrariesSeedFile, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync($"[{{\"id\":\"{libraryId}\"}}]");

        FakeServerUserApiClient api = new([]);
        ServerUserSyncService syncService = new(api);
        await using MediaContext runCtx = CreateContext();
        await runCtx.Init(storage.Object, "valid-token", syncService);

        await using MediaContext verifyCtx = CreateContext();
        Assert.Equal(150, await verifyCtx.LibraryUser.CountAsync(lu => lu.LibraryId == libraryId));
        Assert.False(api.WasCalled);
    }

    [Fact]
    public async Task Init_RepairAddsMissingRowsAndPreservesExistingAssignments()
    {
        Ulid movieLibraryId = Ulid.NewUlid();
        Ulid musicLibraryId = Ulid.NewUlid();
        Guid missingUserId = Guid.Parse("00000000-0000-4000-8000-000000000151");
        Guid scopedUserId = Guid.Parse("00000000-0000-4000-8000-000000000152");
        Guid blockedUserId = Guid.Parse("00000000-0000-4000-8000-000000000153");
        await using (MediaContext seedCtx = CreateContext())
        {
            seedCtx.Libraries.AddRange(
                new Library
                {
                    Id = movieLibraryId,
                    Title = "Movies",
                    Type = "movie",
                },
                new Library
                {
                    Id = musicLibraryId,
                    Title = "Music",
                    Type = "music",
                }
            );
            seedCtx.Users.AddRange(
                new User
                {
                    Id = missingUserId,
                    Email = "missing@example.test",
                    Allowed = true,
                },
                new User
                {
                    Id = scopedUserId,
                    Email = "scoped@example.test",
                    Allowed = true,
                },
                new User
                {
                    Id = blockedUserId,
                    Email = "blocked@example.test",
                    Allowed = false,
                }
            );
            seedCtx.LibraryUser.Add(new(movieLibraryId, scopedUserId));
            await seedCtx.SaveChangesAsync();
        }

        Mock<IStorage> storage = new();
        storage.Setup(s => s.Exists(AppFiles.LibrariesSeedFile)).Returns(true);
        storage
            .Setup(s =>
                s.ReadAllTextAsync(AppFiles.LibrariesSeedFile, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync($"[{{\"id\":\"{movieLibraryId}\"}},{{\"id\":\"{musicLibraryId}\"}}]");
        FakeServerUserApiClient api = new([]);
        ServerUserSyncService syncService = new(api);

        await using (MediaContext firstRun = CreateContext())
            await firstRun.Init(storage.Object, "valid-token", syncService);
        await using (MediaContext secondRun = CreateContext())
            await secondRun.Init(storage.Object, "valid-token", syncService);

        await using MediaContext verifyCtx = CreateContext();
        List<LibraryUser> rows = await verifyCtx.LibraryUser.AsNoTracking().ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Contains(
            rows,
            row => row.LibraryId == movieLibraryId && row.UserId == missingUserId
        );
        Assert.Contains(
            rows,
            row => row.LibraryId == musicLibraryId && row.UserId == missingUserId
        );
        Assert.Contains(rows, row => row.LibraryId == movieLibraryId && row.UserId == scopedUserId);
        Assert.DoesNotContain(
            rows,
            row => row.LibraryId == musicLibraryId && row.UserId == scopedUserId
        );
        Assert.DoesNotContain(rows, row => row.UserId == blockedUserId);
        Assert.False(api.WasCalled);
    }
}
