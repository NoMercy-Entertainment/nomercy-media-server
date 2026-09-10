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
/// Requirement-driven coverage for MusicHub.Devices.cs' ChangeDeviceCommand (the
/// non-TV / no-cast-launch transfer path) and SetDeviceVolumeCommand /
/// ChangeVolumeCommand — the hub methods themselves were previously untested;
/// only the pure MusicVolumeResolver.Clamp helper had coverage (MusicHubVolumeTests).
/// Builds a real MusicHub against the app's DI-configured MediaContext (via
/// NoMercyApiFactory), mocking only the SignalR plumbing and IChromeCastService
/// (native Cast SDK has no test double).
/// </summary>
[Trait("Category", "Characterization")]
public class MusicHubDeviceCommandsTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public MusicHubDeviceCommandsTests(NoMercyApiFactory factory)
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

    private static (Client Client, Mock<ISingleClientProxy> Proxy) MakeClientWithProxy(
        Guid userId,
        string deviceId,
        string type = "web",
        int? volumePercent = null
    )
    {
        Mock<ISingleClientProxy> proxy = new();
        proxy
            .Setup(p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);

        Client client = new()
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = deviceId,
            Endpoint = "/musicHub",
            Type = type,
            Socket = proxy.Object,
            VolumePercent = volumePercent,
        };
        return (client, proxy);
    }

    private MusicHub CreateHub(
        string connectionId,
        Guid userId,
        DeviceBusRegistry? busRegistry = null,
        IChromeCastService? chromeCast = null
    )
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
        AuthManager authManager = _factory.Services.GetRequiredService<AuthManager>();

        // CastPanelWakeLauncher is DI-registered against its OWN
        // IChromeCastService — independent of the chromeCast passed here, which
        // only ever reaches MusicHub's direct field. A custom chromeCast is
        // meaningless unless the launcher wraps that same instance, so build
        // a fresh launcher around it rather than pulling the DI singleton.
        CastPanelWakeLauncher castPanelWakeLauncher = chromeCast is null
            ? _factory.Services.GetRequiredService<CastPanelWakeLauncher>()
            : new(chromeCast, NullLogger<CastPanelWakeLauncher>.Instance);

        MusicDeviceManager musicDeviceManager = new(new());
        MusicPlaylistManager musicPlaylistManager = new(new MusicRepository(contextFactory), new());
        busRegistry ??= new(
            contextFactory,
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
            musicDeviceManager,
            musicPlaylistManager,
            commandHandler,
            Mock.Of<IActivityLogger>(),
            busRegistry,
            castTokenService,
            chromeCast ?? Mock.Of<IChromeCastService>(),
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
            Name = "Device Commands Test User",
            Owner = false,
            Allowed = true,
            Manage = false,
        };
        UserCache.Current.AddUser(user);
        return user;
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

    // =========================================================================
    // ChangeDeviceCommand — non-TV transfer (no Cast launch involved)
    // =========================================================================

    [Fact]
    public async Task ChangeDeviceCommand_NonTvTarget_TransfersDeviceIdAndResolvesVolume()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);

        string phoneConnectionId = Guid.NewGuid().ToString();
        string tabletConnectionId = Guid.NewGuid().ToString();
        string phoneDeviceId = $"phone-{Guid.NewGuid()}";
        string tabletDeviceId = $"tablet-{Guid.NewGuid()}";

        ConnectedClients connectedClients = _factory.GetConnectedClients();
        (Client phoneClient, _) = MakeClientWithProxy(userId, phoneDeviceId, "web");
        (Client tabletClient, _) = MakeClientWithProxy(
            userId,
            tabletDeviceId,
            "web",
            volumePercent: 65
        );
        connectedClients.Clients[phoneConnectionId] = phoneClient;
        connectedClients.Clients[tabletConnectionId] = tabletClient;

        MusicPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<MusicPlayerStateManager>();
        MusicActiveDeviceRegistry registry =
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>();

        PlaylistTrackDto currentTrack = MakeTrack();
        MusicPlayerState state = new()
        {
            DeviceId = phoneDeviceId,
            PlayState = true,
            CurrentItem = currentTrack,
            Playlist = [currentTrack],
            CurrentList = new("/music/albums/test", UriKind.Relative),
            Time = 15_000,
        };
        stateManager.UpdateState(userId, state);
        registry.Set(userId, phoneClient);

        try
        {
            MusicHub hub = CreateHub(phoneConnectionId, userId);

            await hub.ChangeDeviceCommand(tabletDeviceId);

            stateManager.TryGetValue(userId, out MusicPlayerState? after).Should().BeTrue();
            after!.DeviceId.Should().Be(tabletDeviceId);
            // No prior DeviceVolumes entry for the tablet, so it falls back to
            // the target device's own persisted VolumePercent (65).
            after.VolumePercentage.Should().Be(65);
            after.DeviceVolumes[tabletDeviceId].Should().Be(65);

            registry.TryGet(userId, out Device? active).Should().BeTrue();
            active!.DeviceId.Should().Be(tabletDeviceId);
        }
        finally
        {
            Cleanup(userId, user, phoneConnectionId, tabletConnectionId);
        }
    }

    [Fact]
    public async Task ChangeDeviceCommand_NoExistingPlayerState_IsNoOp()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);
        string connectionId = Guid.NewGuid().ToString();

        try
        {
            MusicHub hub = CreateHub(connectionId, userId);

            Func<Task> act = async () => await hub.ChangeDeviceCommand("some-device-id");

            await act.Should().NotThrowAsync();
        }
        finally
        {
            Cleanup(userId, user, connectionId);
        }
    }

    /// <summary>
    /// Reported live, 2026-09-10: tapping play on a phone's local music
    /// mini-player started the track on the living-room TV instead, over the
    /// video it was casting. MusicConnectPlugin.claimActiveForLocalPlaybackStart
    /// sends ChangeDeviceCommand(ownId) before StartPlaybackCommand — with no
    /// player state existing yet (nothing was playing), so the claim used to
    /// hit the no-op branch above and leave the active-device registry
    /// pointing at whatever was active last (a TV that never disconnected
    /// from MusicHub, even while only showing video). The StartPlaybackCommand
    /// that followed then resolved GetOrPromoteActiveDevice to that stale,
    /// still-connected TV instead of the caller who had just explicitly
    /// claimed the device. The claim must land in the registry even when
    /// there is nothing yet to transfer.
    /// </summary>
    [Fact]
    public async Task ChangeDeviceCommand_NoExistingPlayerState_StillClaimsCallerInRegistry()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);
        string phoneConnectionId = Guid.NewGuid().ToString();
        string phoneDeviceId = $"phone-{Guid.NewGuid()}";

        ConnectedClients connectedClients = _factory.GetConnectedClients();
        (Client phoneClient, _) = MakeClientWithProxy(userId, phoneDeviceId, "web");
        connectedClients.Clients[phoneConnectionId] = phoneClient;

        MusicActiveDeviceRegistry registry =
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>();

        try
        {
            MusicHub hub = CreateHub(phoneConnectionId, userId);

            await hub.ChangeDeviceCommand(phoneDeviceId);

            registry.TryGet(userId, out Device? active).Should().BeTrue();
            active!.DeviceId.Should().Be(phoneDeviceId);
        }
        finally
        {
            Cleanup(userId, user, phoneConnectionId);
        }
    }

    /// <summary>
    /// Same reported bug as <see cref="ChangeDeviceCommand_NoExistingPlayerState_StillClaimsCallerInRegistry"/>,
    /// but proven at the altitude the phone client actually reaches: both real
    /// hub entry points MusicConnectPlugin.claimActiveForLocalPlaybackStart calls,
    /// in the order it calls them — ChangeDeviceCommand(ownId) then
    /// StartPlaybackCommand(...) — against a TV that is registered as the
    /// active device from a PRIOR session but never disconnected from MusicHub
    /// (exactly "only showing video, never quit"). The registry-only test above
    /// cannot see whether GetOrPromoteActiveDevice — the actual consumer of the
    /// claim, invoked from StartPlaybackCommand's HandleNewPlayerState — reads
    /// back the phone or falls through to the stale TV; this test reaches that
    /// call site for real, using the real MusicPlaylistManager/MusicRepository
    /// against NoMercyApiFactory's seeded album fixture (no player state exists
    /// yet anywhere for this user, matching "nothing was playing").
    /// </summary>
    [Fact]
    public async Task StartPlaybackCommand_AfterClaimWithStaleTvInRegistry_PlaysOnCallerNotStaleTv()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);
        string tvConnectionId = Guid.NewGuid().ToString();
        string phoneConnectionId = Guid.NewGuid().ToString();
        string tvDeviceId = $"tv-{Guid.NewGuid()}";
        string phoneDeviceId = $"phone-{Guid.NewGuid()}";

        ConnectedClients connectedClients = _factory.GetConnectedClients();
        (Client tvClient, _) = MakeClientWithProxy(userId, tvDeviceId, "tv");
        (Client phoneClient, _) = MakeClientWithProxy(userId, phoneDeviceId, "web");
        connectedClients.Clients[tvConnectionId] = tvClient;
        connectedClients.Clients[phoneConnectionId] = phoneClient;

        MusicPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<MusicPlayerStateManager>();
        MusicActiveDeviceRegistry registry =
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>();

        // The TV was active from a prior session (e.g. it played music earlier,
        // then switched to video) and is STILL CONNECTED to MusicHub — it never
        // disconnected. Nothing is currently playing anywhere, so there is no
        // MusicPlayerState for this user yet.
        registry.Set(userId, tvClient);

        try
        {
            MusicHub hub = CreateHub(phoneConnectionId, userId);

            await hub.ChangeDeviceCommand(phoneDeviceId);
            await hub.StartPlaybackCommand(
                "album",
                NoMercyApiFactory.AlbumId1,
                NoMercyApiFactory.TrackId1
            );

            stateManager.TryGetValue(userId, out MusicPlayerState? state).Should().BeTrue();
            state!
                .DeviceId.Should()
                .Be(
                    phoneDeviceId,
                    "the phone explicitly claimed the device right before starting playback — "
                        + "GetOrPromoteActiveDevice must not fall back to the stale TV"
                );

            registry.TryGet(userId, out Device? active).Should().BeTrue();
            active!.DeviceId.Should().Be(phoneDeviceId);
        }
        finally
        {
            Cleanup(userId, user, tvConnectionId, phoneConnectionId);
        }
    }

    [Fact]
    public async Task ChangeDeviceCommand_UnknownCachedUser_IsNoOp()
    {
        // Deliberately never seeded into UserCache.
        Guid unknownUserId = Guid.NewGuid();
        string connectionId = Guid.NewGuid().ToString();

        MusicHub hub = CreateHub(connectionId, unknownUserId);

        Func<Task> act = async () => await hub.ChangeDeviceCommand("some-device-id");

        await act.Should().NotThrowAsync();
    }

    // =========================================================================
    // ChangeDeviceCommand — TV target, Cast panel-wake LAUNCH
    //
    // Measured live 2026-09-08: a TV can hold an open MusicHub connection
    // (its process alive, socket connected) while its Activity is fully
    // backgrounded — screen off, not foregrounded. Before this fix,
    // "MusicHub-live" alone counted as "already on screen", so neither the
    // software wake_for_music message nor the Cast panel-wake LAUNCH ever
    // fired, and the TV never woke. The fix requires DeviceBusRegistry's own
    // Foreground bit (reported by the client's own /v1/ping) in addition to
    // the SignalR connection before treating a target as truly live.
    // =========================================================================

    private static async Task<bool> WaitForInvocationAsync(
        Mock<IChromeCastService> chromeCast,
        string methodName
    )
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (chromeCast.Invocations.Any(i => i.Method.Name == methodName))
                return true;
            await Task.Delay(50);
        }
        return false;
    }

    private async Task<Device> SeedOwnedTvAsync(
        IDbContextFactory<MediaContext> contextFactory,
        Guid userId,
        string deviceId
    )
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();

        // Device.OwnerUserId is a real FK — SeedTestUser only populates
        // UserCache, so the row needs a matching Users entry to satisfy it.
        if (await ctx.Users.FindAsync(userId) is null)
        {
            ctx.Users.Add(
                new()
                {
                    Id = userId,
                    Email = $"{userId}@nomercy.tv",
                    Name = "Device Commands Test User",
                }
            );
            await ctx.SaveChangesAsync();
        }

        Device tv = new()
        {
            Id = Ulid.NewUlid(),
            DeviceId = deviceId,
            Name = "Bedroom TV",
            Type = "tv",
            LanIp = "192.168.50.21",
            Fingerprint = $"fp-{Guid.NewGuid()}",
            OwnerUserId = userId,
        };
        ctx.Devices.Add(tv);
        await ctx.SaveChangesAsync();
        return tv;
    }

    [Fact]
    public async Task ChangeDeviceCommand_TvMusicHubLiveButNotForeground_StillFiresCastPanelWake()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);
        string tvConnectionId = Guid.NewGuid().ToString();
        string phoneConnectionId = Guid.NewGuid().ToString();
        string tvDeviceId = $"tv-{Guid.NewGuid()}";
        string phoneDeviceId = $"phone-{Guid.NewGuid()}";

        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        Device tv = await SeedOwnedTvAsync(contextFactory, userId, tvDeviceId);

        ConnectedClients connectedClients = _factory.GetConnectedClients();
        (Client tvClient, _) = MakeClientWithProxy(userId, tvDeviceId, "tv");
        // Devices() builds its live-Device view from Client fields, and its
        // entry wins over the DB row's LanIp (MusicDevicesAsync's
        // seenDeviceIds dedupe keeps the first one seen). A real connected
        // TV reports its LAN IP on connect; mirror that here so
        // CastAddress.Resolve has something to resolve, same as production.
        tvClient.Ip = tv.LanIp!;
        // A real connection aligns the in-memory Client's id with the
        // persisted Devices row (ConnectionHub.AlignClientWithPersistedDevice)
        // — GetStatus is keyed by that id, so the fixture has to match it too.
        tvClient.Id = tv.Id;
        (Client phoneClient, _) = MakeClientWithProxy(userId, phoneDeviceId, "web");
        connectedClients.Clients[tvConnectionId] = tvClient;
        connectedClients.Clients[phoneConnectionId] = phoneClient;

        DeviceBusRegistry busRegistry = new(
            contextFactory,
            Mock.Of<IHubContext<DeviceHub>>(),
            Mock.Of<ICastMdnsRegistry>()
        );
        // Deliberately NOT calling UpdateStatus — the TV has never reported
        // foreground=true, matching a backgrounded-but-connected app.

        Mock<IChromeCastService> chromeCast = new();
        chromeCast
            .Setup(c => c.FindReceiverNameByIpAsync(It.IsAny<string>()))
            .ReturnsAsync("Bedroom TV");
        chromeCast.Setup(c => c.SelectChromecast(It.IsAny<string>())).Returns(Task.CompletedTask);
        chromeCast
            .Setup(c =>
                c.LaunchAndroidReceiver(It.IsAny<string?>(), It.IsAny<object?>(), It.IsAny<bool>())
            )
            .Returns(Task.CompletedTask);

        MusicPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<MusicPlayerStateManager>();
        MusicActiveDeviceRegistry registry =
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>();

        PlaylistTrackDto currentTrack = MakeTrack();
        MusicPlayerState state = new()
        {
            DeviceId = phoneDeviceId,
            PlayState = true,
            CurrentItem = currentTrack,
            Playlist = [currentTrack],
            CurrentList = new("/music/albums/test", UriKind.Relative),
            Time = 15_000,
        };
        stateManager.UpdateState(userId, state);
        registry.Set(userId, phoneClient);

        try
        {
            MusicHub hub = CreateHub(
                tvConnectionId,
                userId,
                busRegistry: busRegistry,
                chromeCast: chromeCast.Object
            );

            await hub.ChangeDeviceCommand(tvDeviceId);

            bool launched = await WaitForInvocationAsync(
                chromeCast,
                nameof(IChromeCastService.LaunchAndroidReceiver)
            );
            launched
                .Should()
                .BeTrue(
                    "a MusicHub-connected TV that never reported foreground=true is not "
                        + "actually on screen, and the panel-wake LAUNCH must still fire"
                );
        }
        finally
        {
            Cleanup(userId, user, tvConnectionId, phoneConnectionId);
        }
    }

    [Fact]
    public async Task ChangeDeviceCommand_TvMusicHubLiveAndForeground_NeverFiresCastPanelWake()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);
        string tvConnectionId = Guid.NewGuid().ToString();
        string phoneConnectionId = Guid.NewGuid().ToString();
        string tvDeviceId = $"tv-{Guid.NewGuid()}";
        string phoneDeviceId = $"phone-{Guid.NewGuid()}";

        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        Device tv = await SeedOwnedTvAsync(contextFactory, userId, tvDeviceId);

        ConnectedClients connectedClients = _factory.GetConnectedClients();
        (Client tvClient, _) = MakeClientWithProxy(userId, tvDeviceId, "tv");
        // Devices() builds its live-Device view from Client fields, and its
        // entry wins over the DB row's LanIp (MusicDevicesAsync's
        // seenDeviceIds dedupe keeps the first one seen). A real connected
        // TV reports its LAN IP on connect; mirror that here so
        // CastAddress.Resolve has something to resolve, same as production.
        tvClient.Ip = tv.LanIp!;
        // A real connection aligns the in-memory Client's id with the
        // persisted Devices row (ConnectionHub.AlignClientWithPersistedDevice)
        // — GetStatus is keyed by that id, so the fixture has to match it too.
        tvClient.Id = tv.Id;
        (Client phoneClient, _) = MakeClientWithProxy(userId, phoneDeviceId, "web");
        connectedClients.Clients[tvConnectionId] = tvClient;
        connectedClients.Clients[phoneConnectionId] = phoneClient;

        DeviceBusRegistry busRegistry = new(
            contextFactory,
            Mock.Of<IHubContext<DeviceHub>>(),
            Mock.Of<ICastMdnsRegistry>()
        );
        // The TV reports itself genuinely on screen — this is the case
        // CastPanelWakeLauncher exists to protect: firing LAUNCH here risks
        // cast_shell missing the running APK and falling back to the Web
        // Receiver over a session that's actually playing (see
        // CastPanelWakeLauncherTests' header comment for the prior incident).
        busRegistry.UpdateStatus(tv.Id, foreground: true, screenOn: true);

        Mock<IChromeCastService> chromeCast = new();
        chromeCast
            .Setup(c => c.FindReceiverNameByIpAsync(It.IsAny<string>()))
            .ReturnsAsync("Bedroom TV");

        MusicPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<MusicPlayerStateManager>();
        MusicActiveDeviceRegistry registry =
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>();

        PlaylistTrackDto currentTrack = MakeTrack();
        MusicPlayerState state = new()
        {
            DeviceId = phoneDeviceId,
            PlayState = true,
            CurrentItem = currentTrack,
            Playlist = [currentTrack],
            CurrentList = new("/music/albums/test", UriKind.Relative),
            Time = 15_000,
        };
        stateManager.UpdateState(userId, state);
        registry.Set(userId, phoneClient);

        try
        {
            MusicHub hub = CreateHub(
                tvConnectionId,
                userId,
                busRegistry: busRegistry,
                chromeCast: chromeCast.Object
            );

            await hub.ChangeDeviceCommand(tvDeviceId);

            // Give the fire-and-forget path the same window the positive test
            // waits up to, then confirm it never called through.
            await Task.Delay(300);
            chromeCast.Verify(
                c =>
                    c.LaunchAndroidReceiver(
                        It.IsAny<string?>(),
                        It.IsAny<object?>(),
                        It.IsAny<bool>()
                    ),
                Times.Never
            );
        }
        finally
        {
            Cleanup(userId, user, tvConnectionId, phoneConnectionId);
        }
    }

    // =========================================================================
    // SetDeviceVolumeCommand / ChangeVolumeCommand
    // =========================================================================

    [Fact]
    public async Task SetDeviceVolumeCommand_ActiveDeviceTarget_UpdatesScopedVolumePercentage()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);
        string connectionId = Guid.NewGuid().ToString();
        string deviceId = $"tv-{Guid.NewGuid()}";

        ConnectedClients connectedClients = _factory.GetConnectedClients();
        (Client client, _) = MakeClientWithProxy(userId, deviceId, "tv");
        connectedClients.Clients[connectionId] = client;

        MusicPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<MusicPlayerStateManager>();
        MusicActiveDeviceRegistry registry =
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>();

        MusicPlayerState state = new() { DeviceId = deviceId, PlayState = true };
        stateManager.UpdateState(userId, state);
        registry.Set(userId, client);

        try
        {
            MusicHub hub = CreateHub(connectionId, userId);

            await hub.SetDeviceVolumeCommand(deviceId, 77);

            state.VolumePercentage.Should().Be(77);
            state.DeviceVolumes[deviceId].Should().Be(77);
            client.VolumePercent.Should().Be(77);
        }
        finally
        {
            Cleanup(userId, user, connectionId);
        }
    }

    [Fact]
    public async Task SetDeviceVolumeCommand_NonActiveDeviceTarget_UpdatesDeviceVolumesOnly()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);
        string activeConnectionId = Guid.NewGuid().ToString();
        string passiveConnectionId = Guid.NewGuid().ToString();
        string activeDeviceId = $"tv-{Guid.NewGuid()}";
        string passiveDeviceId = $"phone-{Guid.NewGuid()}";

        ConnectedClients connectedClients = _factory.GetConnectedClients();
        (Client activeClient, _) = MakeClientWithProxy(userId, activeDeviceId, "tv");
        (Client passiveClient, _) = MakeClientWithProxy(userId, passiveDeviceId, "web");
        connectedClients.Clients[activeConnectionId] = activeClient;
        connectedClients.Clients[passiveConnectionId] = passiveClient;

        MusicPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<MusicPlayerStateManager>();
        MusicActiveDeviceRegistry registry =
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>();

        MusicPlayerState state = new()
        {
            DeviceId = activeDeviceId,
            PlayState = true,
            VolumePercentage = 50,
        };
        stateManager.UpdateState(userId, state);
        registry.Set(userId, activeClient);

        try
        {
            // The PASSIVE phone adjusts ITS OWN slider — must not move the
            // active TV's scoped VolumePercentage.
            MusicHub hub = CreateHub(passiveConnectionId, userId);

            await hub.SetDeviceVolumeCommand(passiveDeviceId, 20);

            state.VolumePercentage.Should().Be(50);
            state.DeviceVolumes[passiveDeviceId].Should().Be(20);
            passiveClient.VolumePercent.Should().Be(20);
        }
        finally
        {
            Cleanup(userId, user, activeConnectionId, passiveConnectionId);
        }
    }

    [Fact]
    public async Task SetDeviceVolumeCommand_NullVolume_IsNoOp()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);
        string connectionId = Guid.NewGuid().ToString();

        try
        {
            MusicHub hub = CreateHub(connectionId, userId);

            Func<Task> act = async () => await hub.SetDeviceVolumeCommand("any-device", null);

            await act.Should().NotThrowAsync();
        }
        finally
        {
            Cleanup(userId, user, connectionId);
        }
    }

    [Fact]
    public async Task SetDeviceVolumeCommand_UnknownDeviceIdAndNoActiveDevice_IsNoOp()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);
        string connectionId = Guid.NewGuid().ToString();

        try
        {
            MusicHub hub = CreateHub(connectionId, userId);

            Func<Task> act = async () =>
                await hub.SetDeviceVolumeCommand("device-that-does-not-exist", 50);

            await act.Should().NotThrowAsync();
        }
        finally
        {
            Cleanup(userId, user, connectionId);
        }
    }

    [Fact]
    public async Task ChangeVolumeCommand_NullDeviceId_TargetsCurrentActiveDevice()
    {
        Guid userId = Guid.NewGuid();
        User user = SeedTestUser(userId);
        string connectionId = Guid.NewGuid().ToString();
        string activeDeviceId = $"tv-{Guid.NewGuid()}";

        ConnectedClients connectedClients = _factory.GetConnectedClients();
        (Client activeClient, _) = MakeClientWithProxy(userId, activeDeviceId, "tv");
        connectedClients.Clients[connectionId] = activeClient;

        MusicPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<MusicPlayerStateManager>();
        MusicActiveDeviceRegistry registry =
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>();

        MusicPlayerState state = new() { DeviceId = activeDeviceId, PlayState = true };
        stateManager.UpdateState(userId, state);
        registry.Set(userId, activeClient);

        try
        {
            MusicHub hub = CreateHub(connectionId, userId);

            await hub.ChangeVolumeCommand(33);

            state.VolumePercentage.Should().Be(33);
            activeClient.VolumePercent.Should().Be(33);
        }
        finally
        {
            Cleanup(userId, user, connectionId);
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
