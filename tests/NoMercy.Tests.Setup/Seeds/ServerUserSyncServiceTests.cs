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
using NoMercy.Storage;

namespace NoMercy.Tests.Setup.Seeds;

/// <summary>
/// Covers the regression this slice fixes: <see cref="UsersSeed"/> used to be
/// gated on the Users table being empty, so a user invited after the server's
/// first boot was never synced. <see cref="ServerUserSyncService"/> is the
/// ongoing-reconciliation path the recurring cron job calls — it must upsert
/// every run regardless of table state, and must revoke access for users no
/// longer returned upstream without ever touching the owner.
/// </summary>
[Trait("Category", "Unit")]
public class ServerUserSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private static readonly Mock<IStorage> NoLibrariesStorage = new();

    public ServerUserSyncServiceTests()
    {
        _connection = new("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();

        // No libraries.jsonc on disk in these tests — Exists() defaults to
        // false on an unconfigured Moq mock, so library-access seeding is a
        // no-op and out of scope for these assertions.
    }

    public void Dispose() => _connection.Dispose();

    private MediaContext CreateContext() => new(_options);

    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InvitedUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static User OwnerUser() =>
        new()
        {
            Id = OwnerId,
            Email = "owner@example.com",
            Name = "Owner",
            Allowed = true,
            Owner = true,
            AudioTranscoding = true,
            NoTranscoding = true,
            VideoTranscoding = true,
        };

    [Fact]
    public async Task SyncAsync_AddsNewlyInvitedUser_ToNonEmptyUsersTable()
    {
        // Regression case: the Users table already has the owner (from a prior
        // boot) when a second person accepts an invite. The old UsersSeed.Init
        // would never re-run once any user existed — this must not.
        await using MediaContext seedCtx = CreateContext();
        seedCtx.Users.Add(OwnerUser());
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
            new()
            {
                UserId = InvitedUserId.ToString(),
                Name = "Invited",
                Email = "invited@example.com",
                IsOwner = false,
                Enabled = true,
            },
        ]);
        ServerUserSyncService sut = new(api);

        await using MediaContext runCtx = CreateContext();
        ServerUserSyncResult result = await sut.SyncAsync(
            runCtx,
            NoLibrariesStorage.Object,
            "valid-token"
        );

        Assert.True(result.Attempted);
        Assert.Equal(2, result.UpstreamUserCount);

        await using MediaContext assertCtx = CreateContext();
        User? invited = await assertCtx.Users.FirstOrDefaultAsync(u => u.Id == InvitedUserId);
        Assert.NotNull(invited);
        Assert.True(invited!.Allowed);
        Assert.False(invited.Owner);

        Assert.Equal(2, await assertCtx.Users.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_RevokesAccess_ForUserNoLongerReturnedUpstream_OwnerPreserved()
    {
        await using MediaContext seedCtx = CreateContext();
        seedCtx.Users.Add(OwnerUser());
        seedCtx.Users.Add(
            new()
            {
                Id = InvitedUserId,
                Email = "invited@example.com",
                Name = "Invited",
                Allowed = true,
                Owner = false,
                AudioTranscoding = true,
                NoTranscoding = true,
                VideoTranscoding = true,
            }
        );
        await seedCtx.SaveChangesAsync();

        // Upstream now only returns the owner — the invite was removed/declined.
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
        ServerUserSyncService sut = new(api);

        await using MediaContext runCtx = CreateContext();
        ServerUserSyncResult result = await sut.SyncAsync(
            runCtx,
            NoLibrariesStorage.Object,
            "valid-token"
        );

        Assert.Equal(1, result.RevokedCount);

        await using MediaContext assertCtx = CreateContext();
        User? revoked = await assertCtx.Users.FirstOrDefaultAsync(u => u.Id == InvitedUserId);
        Assert.NotNull(revoked);
        Assert.False(revoked!.Allowed);
        Assert.False(revoked.AudioTranscoding);
        Assert.False(revoked.VideoTranscoding);
        Assert.False(revoked.NoTranscoding);

        User? owner = await assertCtx.Users.FirstOrDefaultAsync(u => u.Id == OwnerId);
        Assert.NotNull(owner);
        Assert.True(owner!.Owner);
        Assert.True(owner.Allowed);
    }

    [Fact]
    public async Task SyncAsync_NoAccessToken_SkipsWithoutThrowing_AndNeverCallsApi()
    {
        FakeServerUserApiClient api = new([]);
        ServerUserSyncService sut = new(api);

        await using MediaContext runCtx = CreateContext();
        ServerUserSyncResult result = await sut.SyncAsync(
            runCtx,
            NoLibrariesStorage.Object,
            accessToken: null
        );

        Assert.False(result.Attempted);
        Assert.Equal(0, result.UpstreamUserCount);
        Assert.False(api.WasCalled);
    }

    [Fact]
    public async Task SyncAsync_IsIdempotent_RunningTwiceKeepsSingleRowPerUser()
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
        ServerUserSyncService sut = new(api);

        await using MediaContext firstRun = CreateContext();
        await sut.SyncAsync(firstRun, NoLibrariesStorage.Object, "valid-token");

        await using MediaContext secondRun = CreateContext();
        await sut.SyncAsync(secondRun, NoLibrariesStorage.Object, "valid-token");

        await using MediaContext assertCtx = CreateContext();
        Assert.Equal(1, await assertCtx.Users.CountAsync());
    }

    /// <summary>
    /// api.nomercy.tv responding 200 with an empty/malformed body must surface as
    /// a hard failure (ServerUserApiClient.ParseResponse throws) rather than an
    /// empty user list — SyncAsync must let that exception propagate BEFORE any
    /// database write, so a transient bad response can never mass-revoke.
    /// </summary>
    [Fact]
    public async Task SyncAsync_ApiClientThrows_PropagatesException_AndRevokesNoOne()
    {
        await using MediaContext seedCtx = CreateContext();
        seedCtx.Users.Add(OwnerUser());
        seedCtx.Users.Add(
            new()
            {
                Id = InvitedUserId,
                Email = "invited@example.com",
                Name = "Invited",
                Allowed = true,
                Owner = false,
                AudioTranscoding = true,
                NoTranscoding = true,
                VideoTranscoding = true,
            }
        );
        await seedCtx.SaveChangesAsync();

        FakeServerUserApiClient api = new(
            [],
            throwInstead: new InvalidOperationException(
                "server-users response was empty or unparseable"
            )
        );
        ServerUserSyncService sut = new(api);

        await using MediaContext runCtx = CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SyncAsync(runCtx, NoLibrariesStorage.Object, "valid-token")
        );

        await using MediaContext assertCtx = CreateContext();
        User? invited = await assertCtx.Users.FirstOrDefaultAsync(u => u.Id == InvitedUserId);
        Assert.NotNull(invited);
        Assert.True(invited!.Allowed);
    }

    /// <summary>
    /// Self floor check: with_self=true means an authoritative response always
    /// contains this server's own owner. An empty (or owner-omitting) list must
    /// abort the whole reconcile — no upsert, no revoke — instead of being read
    /// as "upstream really does have zero users now".
    /// </summary>
    [Fact]
    public async Task SyncAsync_ResponseOmitsLocalOwner_AbortsReconcile_NoRevoke()
    {
        await using MediaContext seedCtx = CreateContext();
        seedCtx.Users.Add(OwnerUser());
        seedCtx.Users.Add(
            new()
            {
                Id = InvitedUserId,
                Email = "invited@example.com",
                Name = "Invited",
                Allowed = true,
                Owner = false,
                AudioTranscoding = true,
                NoTranscoding = true,
                VideoTranscoding = true,
            }
        );
        await seedCtx.SaveChangesAsync();

        // A well-formed but empty payload — the "silently treated as zero users"
        // shape the compat-gate flagged, not a thrown exception.
        FakeServerUserApiClient api = new([]);
        ServerUserSyncService sut = new(api);

        await using MediaContext runCtx = CreateContext();
        ServerUserSyncResult result = await sut.SyncAsync(
            runCtx,
            NoLibrariesStorage.Object,
            "valid-token"
        );

        Assert.False(result.Attempted);
        Assert.Equal(0, result.RevokedCount);

        await using MediaContext assertCtx = CreateContext();
        User? invited = await assertCtx.Users.FirstOrDefaultAsync(u => u.Id == InvitedUserId);
        Assert.NotNull(invited);
        Assert.True(invited!.Allowed);

        User? owner = await assertCtx.Users.FirstOrDefaultAsync(u => u.Id == OwnerId);
        Assert.NotNull(owner);
        Assert.True(owner!.Owner);
    }

    /// <summary>
    /// Transcoding flags are local per-user config, not upstream membership state.
    /// A stable server-users list (nothing invited or removed) must not silently
    /// re-stomp a local override back to serverUser.Enabled every cycle.
    /// </summary>
    [Fact]
    public async Task SyncAsync_PreservesLocalTranscodingOverride_AcrossSync()
    {
        await using MediaContext seedCtx = CreateContext();
        seedCtx.Users.Add(OwnerUser());
        seedCtx.Users.Add(
            new()
            {
                Id = InvitedUserId,
                Email = "invited@example.com",
                Name = "Invited",
                Allowed = true,
                Owner = false,
                // Locally overridden away from what upstream Enabled=true implies.
                AudioTranscoding = false,
                NoTranscoding = false,
                VideoTranscoding = false,
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
            new()
            {
                UserId = InvitedUserId.ToString(),
                Name = "Invited",
                Email = "invited@example.com",
                IsOwner = false,
                Enabled = true,
            },
        ]);
        ServerUserSyncService sut = new(api);

        await using MediaContext runCtx = CreateContext();
        await sut.SyncAsync(runCtx, NoLibrariesStorage.Object, "valid-token");

        await using MediaContext assertCtx = CreateContext();
        User? invited = await assertCtx.Users.FirstOrDefaultAsync(u => u.Id == InvitedUserId);
        Assert.NotNull(invited);
        Assert.True(invited!.Allowed);
        Assert.False(invited.AudioTranscoding);
        Assert.False(invited.NoTranscoding);
        Assert.False(invited.VideoTranscoding);
    }
}
