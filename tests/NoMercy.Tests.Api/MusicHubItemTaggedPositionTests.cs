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

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Hubs;
using NoMercy.Api.Services.Music;
using NoMercy.Api.WebSockets;
using NoMercy.Authorization;
using NoMercy.Data.Activity;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Auth;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Cast;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Covers the item-tagged twins of ReportPositionCommand/CurrentTimeCommand:
/// ReportPositionForItemCommand must accept a report tagged with the CURRENT
/// item, drop one tagged with any other item (no mutation, no broadcast), and
/// keep behaving like the untagged command when no tag is supplied at all —
/// the same real-DI MusicHub harness MusicHubActiveDeviceDisconnectTests uses.
/// </summary>
[Trait("Category", "Characterization")]
public class MusicHubItemTaggedPositionTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public MusicHubItemTaggedPositionTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _factory.CreateClient();
    }

    private static PlaylistTrackDto MakeTrack()
    {
        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Track",
            Duration = "180",
            Filename = "test.mp3",
            Folder = "/music/",
            FolderId = Ulid.NewUlid(),
        };
        return new(track, "US");
    }

    private static Client MakeClient(Guid userId, string deviceId, string type = "web")
    {
        return new()
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = deviceId,
            Endpoint = "/musicHub",
            Type = type,
            Socket = Mock.Of<ISingleClientProxy>(),
        };
    }

    private MusicHub CreateHub(string connectionId, Guid userId)
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        ConnectedClients connectedClients = _factory.GetConnectedClients();
        IClientMessenger clientMessenger = _factory.Services.GetRequiredService<IClientMessenger>();
        MusicPlaybackService musicPlaybackService =
            _factory.Services.GetRequiredService<MusicPlaybackService>();
        MusicPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<MusicPlayerStateManager>();
        MusicPlaybackCommandHandler commandHandler =
            _factory.Services.GetRequiredService<MusicPlaybackCommandHandler>();
        MusicActiveDeviceRegistry activeDeviceRegistry =
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>();
        CastPanelWakeLauncher castPanelWakeLauncher =
            _factory.Services.GetRequiredService<CastPanelWakeLauncher>();
        AuthManager authManager = _factory.Services.GetRequiredService<AuthManager>();

        MusicPlaylistManager musicPlaylistManager = new(new MusicRepository(contextFactory), new());
        DeviceBusRegistry busRegistry = new(
            new DeviceStateRepository(contextFactory),
            Mock.Of<IHubContext<DeviceHub>>(),
            Mock.Of<ICastMdnsRegistry>()
        );
        CastSessionTokenService castTokenService = new(authManager, new AuthTokenStore());

        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/musicHub";

        MusicHub hub = new(
            NullLogger<MusicHub>.Instance,
            new HttpContextAccessorStub(httpContext),
            contextFactory,
            connectedClients,
            clientMessenger,
            musicPlaybackService,
            stateManager,
            musicPlaylistManager,
            commandHandler,
            Mock.Of<IActivityLogger>(),
            busRegistry,
            castTokenService,
            Mock.Of<IChromeCastService>(),
            castPanelWakeLauncher,
            activeDeviceRegistry
        );

        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth")
        );

        Mock<HubCallerContext> context = new();
        context.Setup(c => c.User).Returns(principal);
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

        Mock<ISingleClientProxy> callerProxy = new();
        Mock<ISingleClientProxy> userProxy = new();

        Mock<IHubCallerClients> clients = new();
        clients.Setup(c => c.Caller).Returns(callerProxy.Object);
        clients.Setup(c => c.User(It.IsAny<string>())).Returns(userProxy.Object);

        hub.Context = context.Object;
        hub.Clients = clients.Object;

        return hub;
    }

    private static User SeedTestUser(Guid userId)
    {
        User user = new()
        {
            Id = userId,
            Email = $"{userId}@nomercy.tv",
            Name = "Item Tag Test User",
            Owner = false,
            Allowed = true,
            Manage = false,
        };
        UserCache.Current.AddUser(user);
        return user;
    }

    private (
        Guid UserId,
        User User,
        string ActiveConnectionId,
        string ActiveDeviceId,
        PlaylistTrackDto CurrentTrack,
        MusicPlayerState State
    ) SeedActiveSession()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);

        string activeConnectionId = Guid.NewGuid().ToString();
        string activeDeviceId = $"web-{Guid.NewGuid()}";

        ConnectedClients connectedClients = _factory.GetConnectedClients();
        Client activeClient = MakeClient(userId, activeDeviceId);
        connectedClients.Clients[activeConnectionId] = activeClient;

        MusicPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<MusicPlayerStateManager>();
        MusicActiveDeviceRegistry registry =
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>();

        PlaylistTrackDto currentTrack = MakeTrack();
        MusicPlayerState state = new()
        {
            DeviceId = activeDeviceId,
            PlayState = true,
            CurrentItem = currentTrack,
            Playlist = [MakeTrack()],
            CurrentList = new("/music/albums/test", UriKind.Relative),
            Time = 10_000,
            LastActiveHeartbeatUtc = DateTime.UtcNow,
        };
        stateManager.UpdateState(userId, state);
        registry.Set(userId, activeClient);

        return (userId, user, activeConnectionId, activeDeviceId, currentTrack, state);
    }

    private void Cleanup(Guid userId, User user, params string[] connectionIds)
    {
        ConnectedClients connectedClients = _factory.GetConnectedClients();
        foreach (string connectionId in connectionIds)
            connectedClients.Clients.TryRemove(connectionId, out _);

        _factory.Services.GetRequiredService<MusicPlayerStateManager>().RemoveState(userId);
        _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>().Remove(userId);
        UserCache.Current.RemoveUser(user);
    }

    [Fact]
    public async Task ReportPositionForItemCommand_MatchingItemId_AcceptsAndUpdatesPosition()
    {
        (
            Guid userId,
            User user,
            string activeConnectionId,
            string _,
            PlaylistTrackDto currentTrack,
            MusicPlayerState state
        ) = SeedActiveSession();

        try
        {
            MusicHub hub = CreateHub(activeConnectionId, userId);
            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await hub.ReportPositionForItemCommand(5_000, currentTrack.Id.ToString());

            state.Time.Should().Be(5_000);
            state.PositionCapturedAtMs.Should().BeGreaterThanOrEqualTo(before);
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId);
        }
    }

    [Fact]
    public async Task ReportPositionForItemCommand_MismatchedItemId_DropsReport()
    {
        (
            Guid userId,
            User user,
            string activeConnectionId,
            string _,
            PlaylistTrackDto _,
            MusicPlayerState state
        ) = SeedActiveSession();

        try
        {
            MusicHub hub = CreateHub(activeConnectionId, userId);
            long originalPositionCapturedAtMs = state.PositionCapturedAtMs;

            await hub.ReportPositionForItemCommand(5_000, Guid.NewGuid().ToString());

            // Untouched — the stale-tagged report never reaches the mutation.
            state.Time.Should().Be(10_000);
            state.PositionCapturedAtMs.Should().Be(originalPositionCapturedAtMs);
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId);
        }
    }

    [Fact]
    public async Task ReportPositionForItemCommand_NullItemId_BehavesLikeUntaggedReport()
    {
        (
            Guid userId,
            User user,
            string activeConnectionId,
            string _,
            PlaylistTrackDto _,
            MusicPlayerState state
        ) = SeedActiveSession();

        try
        {
            MusicHub hub = CreateHub(activeConnectionId, userId);

            await hub.ReportPositionForItemCommand(7_500, null);

            state.Time.Should().Be(7_500);
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId);
        }
    }

    [Fact]
    public async Task ReportPositionCommand_Untagged_StillWorks_Unchanged()
    {
        // Regression: the pre-existing untagged command must keep working
        // exactly as before — it has no itemId parameter to be stale-checked.
        (
            Guid userId,
            User user,
            string activeConnectionId,
            string _,
            PlaylistTrackDto _,
            MusicPlayerState state
        ) = SeedActiveSession();

        try
        {
            MusicHub hub = CreateHub(activeConnectionId, userId);

            await hub.ReportPositionCommand(6_000);

            state.Time.Should().Be(6_000);
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId);
        }
    }

    [Fact]
    public async Task CurrentTimeForItemCommand_ConvertsSecondsToMilliseconds()
    {
        (
            Guid userId,
            User user,
            string activeConnectionId,
            string _,
            PlaylistTrackDto currentTrack,
            MusicPlayerState state
        ) = SeedActiveSession();

        try
        {
            MusicHub hub = CreateHub(activeConnectionId, userId);

            await hub.CurrentTimeForItemCommand(3.5, currentTrack.Id.ToString());

            state.Time.Should().Be(3_500);
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId);
        }
    }

    // ── Liveness-first reordering (live-incident regression) ────────────────
    // TryRefreshHeartbeat must run BEFORE the ignore-window and stale-item drop
    // gates so a report from the ACTIVE device still proves it's alive even when
    // its position value gets rejected. A passive device's report must stay a
    // complete no-op for both liveness and position.

    [Fact]
    public async Task ReportPositionForItemCommand_InsideIgnoreWindow_RefreshesHeartbeat_ButDoesNotMutatePosition()
    {
        (
            Guid userId,
            User user,
            string activeConnectionId,
            string _,
            PlaylistTrackDto currentTrack,
            MusicPlayerState state
        ) = SeedActiveSession();

        try
        {
            state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(5);
            state.LastActiveHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10);

            MusicHub hub = CreateHub(activeConnectionId, userId);

            await hub.ReportPositionForItemCommand(5_000, currentTrack.Id.ToString());

            // Dropped by the ignore window — position must be untouched...
            state.Time.Should().Be(10_000);
            // ...but the active device still proved it's alive right now.
            state
                .LastActiveHeartbeatUtc.Should()
                .BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId);
        }
    }

    [Fact]
    public async Task ReportPositionForItemCommand_MismatchedItemId_RefreshesHeartbeat_ButDoesNotMutatePosition()
    {
        (
            Guid userId,
            User user,
            string activeConnectionId,
            string _,
            PlaylistTrackDto _,
            MusicPlayerState state
        ) = SeedActiveSession();

        try
        {
            state.LastActiveHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10);

            MusicHub hub = CreateHub(activeConnectionId, userId);

            await hub.ReportPositionForItemCommand(5_000, Guid.NewGuid().ToString());

            // Dropped as stale-item — position untouched...
            state.Time.Should().Be(10_000);
            // ...but the active device still proved it's alive right now: a report
            // that arrives just after a track change is still proof the connection
            // is up, even though its position value can't be trusted.
            state
                .LastActiveHeartbeatUtc.Should()
                .BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId);
        }
    }

    [Fact]
    public async Task ReportPositionForItemCommand_FromPassiveDevice_RefreshesNothing()
    {
        (
            Guid userId,
            User user,
            string activeConnectionId,
            string _,
            PlaylistTrackDto currentTrack,
            MusicPlayerState state
        ) = SeedActiveSession();

        string passiveConnectionId = Guid.NewGuid().ToString();
        ConnectedClients connectedClients = _factory.GetConnectedClients();
        connectedClients.Clients[passiveConnectionId] = MakeClient(
            userId,
            $"passive-{Guid.NewGuid()}"
        );

        DateTime originalHeartbeat = DateTime.UtcNow.AddSeconds(-10);
        state.LastActiveHeartbeatUtc = originalHeartbeat;

        try
        {
            MusicHub hub = CreateHub(passiveConnectionId, userId);

            await hub.ReportPositionForItemCommand(5_000, currentTrack.Id.ToString());

            // A complete no-op: neither the position nor the liveness clock moves —
            // a passive mirror must never mask a truly-dead active device.
            state.Time.Should().Be(10_000);
            state.LastActiveHeartbeatUtc.Should().Be(originalHeartbeat);
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId, passiveConnectionId);
        }
    }

    [Fact]
    public async Task ReportPositionCommand_InsideIgnoreWindow_RefreshesHeartbeat_ButDoesNotMutatePosition()
    {
        (
            Guid userId,
            User user,
            string activeConnectionId,
            string _,
            PlaylistTrackDto _,
            MusicPlayerState state
        ) = SeedActiveSession();

        try
        {
            state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(5);
            state.LastActiveHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10);

            MusicHub hub = CreateHub(activeConnectionId, userId);

            await hub.ReportPositionCommand(5_000);

            state.Time.Should().Be(10_000);
            state
                .LastActiveHeartbeatUtc.Should()
                .BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId);
        }
    }

    [Fact]
    public async Task ReportPositionCommand_FromPassiveDevice_RefreshesNothing()
    {
        (
            Guid userId,
            User user,
            string activeConnectionId,
            string _,
            PlaylistTrackDto _,
            MusicPlayerState state
        ) = SeedActiveSession();

        string passiveConnectionId = Guid.NewGuid().ToString();
        ConnectedClients connectedClients = _factory.GetConnectedClients();
        connectedClients.Clients[passiveConnectionId] = MakeClient(
            userId,
            $"passive-{Guid.NewGuid()}"
        );

        DateTime originalHeartbeat = DateTime.UtcNow.AddSeconds(-10);
        state.LastActiveHeartbeatUtc = originalHeartbeat;

        try
        {
            MusicHub hub = CreateHub(passiveConnectionId, userId);

            await hub.ReportPositionCommand(5_000);

            state.Time.Should().Be(10_000);
            state.LastActiveHeartbeatUtc.Should().Be(originalHeartbeat);
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId, passiveConnectionId);
        }
    }

    // Minimal IHttpContextAccessor stand-in — the real implementation is an
    // AsyncLocal-backed singleton unsuited to constructing an isolated
    // HttpContext per test.
    private sealed class HttpContextAccessorStub(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}
