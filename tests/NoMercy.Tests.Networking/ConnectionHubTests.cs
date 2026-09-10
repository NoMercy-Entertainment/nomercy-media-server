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
using Microsoft.Extensions.Primitives;
using Moq;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Users;
using NoMercy.Networking;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.Tests.Networking.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: ConnectionHub is the base every SignalR hub in the server
/// inherits — its device-upsert-on-connect, device-inactive-on-disconnect,
/// and per-user Devices() filtering are relied on by every derived hub
/// (Video/Music/Dashboard/Cast/Ripper). A user with no cached DB record must
/// never be added to ConnectedClients (so no broadcast targets a ghost
/// connection); a connecting client_id must be upserted and preserve the
/// persisted CustomName/VolumePercent across reconnects; Devices() must only
/// ever return the CALLING user's OWN devices on THIS hub endpoint, never
/// another user's or another hub's connections.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectionHubTests : IDisposable
{
    private sealed class TestableConnectionHub : ConnectionHub
    {
        public TestableConnectionHub(
            IHttpContextAccessor httpContextAccessor,
            IDbContextFactory<MediaContext> contextFactory,
            ConnectedClients connectedClients,
            IActivityLogger activityLogger
        )
            : base(httpContextAccessor, contextFactory, connectedClients, activityLogger) { }

        public IUserCache ExposedUserCacheService => UserCacheService;
        public IMediaAuthorizationPolicy ExposedAuthPolicy => AuthPolicy;
    }

    private readonly ConnectionHubTestDbContextFactory _disposableContextFactory = new();
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly ConnectedClients _connectedClients = new();
    private readonly Mock<IActivityLogger> _activityLogger = new();

    public ConnectionHubTests()
    {
        _contextFactory = _disposableContextFactory;
        _activityLogger
            .Setup(l =>
                l.LogConnectionAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<Ulid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _disposableContextFactory.Dispose();

    private TestableConnectionHub BuildHub(
        User? cachedUser,
        out Mock<ISingleClientProxy> callerProxy,
        out Mock<ISingleClientProxy> userProxy,
        string path = "/videoHub",
        IQueryCollection? query = null,
        Guid? connectingUserId = null
    )
    {
        UserCache userCache = new();
        if (cachedUser is not null)
            userCache.AddUser(cachedUser);

        ServiceCollection services = new();
        services.AddSingleton<IUserCache>(userCache);
        ServiceProvider provider = services.BuildServiceProvider();

        DefaultHttpContext httpContext = new() { RequestServices = provider };
        httpContext.Request.Path = path;
        if (query is not null)
            httpContext.Request.Query = query;
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.7");

        TestableConnectionHub hub = new(
            new HttpContextAccessorStub(httpContext),
            _contextFactory,
            _connectedClients,
            _activityLogger.Object
        );

        Guid userId = connectingUserId ?? cachedUser?.Id ?? Guid.NewGuid();
        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth")
        );

        Mock<HubCallerContext> context = new();
        context.Setup(c => c.User).Returns(principal);
        context.Setup(c => c.ConnectionId).Returns(Guid.NewGuid().ToString());
        context.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

        callerProxy = new();
        userProxy = new();
        Mock<IHubCallerClients> clients = new();
        clients.Setup(c => c.Caller).Returns(callerProxy.Object);
        clients.Setup(c => c.User(It.IsAny<string>())).Returns(userProxy.Object);

        hub.Context = context.Object;
        hub.Clients = clients.Object;

        return hub;
    }

    [Fact]
    public async Task OnConnectedAsync_UserNotInCache_DoesNotAddToConnectedClients()
    {
        TestableConnectionHub hub = BuildHub(
            cachedUser: null,
            out _,
            out _,
            connectingUserId: Guid.NewGuid()
        );

        await hub.OnConnectedAsync();

        Assert.Empty(_connectedClients.Clients);
    }

    [Fact]
    public async Task OnConnectedAsync_UserInCache_AddsToConnectedClients()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        QueryCollection query = new(
            new Dictionary<string, StringValues>
            {
                ["client_id"] = "device-abc",
                ["client_name"] = "Living Room TV",
                ["client_type"] = "tv",
            }
        );

        TestableConnectionHub hub = BuildHub(user, out _, out _, query: query);

        await hub.OnConnectedAsync();

        Assert.Single(_connectedClients.Clients);
        Client stored = _connectedClients.Clients.Values.Single();
        Assert.Equal(user.Id, stored.Sub);
        Assert.Equal("device-abc", stored.DeviceId);
        Assert.Equal("Living Room TV", stored.Name);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task OnConnectedAsync_WithClientId_UpsertsDeviceRow()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        QueryCollection query = new(
            new Dictionary<string, StringValues>
            {
                ["client_id"] = "device-upsert-test",
                ["client_type"] = "tv",
            }
        );
        TestableConnectionHub hub = BuildHub(user, out _, out _, query: query);

        await hub.OnConnectedAsync();

        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        Device? device = await ctx.Devices.FirstOrDefaultAsync(d =>
            d.DeviceId == "device-upsert-test"
        );
        Assert.NotNull(device);
        Assert.True(device!.IsActive);
    }

    /// <summary>
    /// WsConnectedAt was, before this fix, written only by the separate
    /// device-bus WebSocket — never by an ordinary hub connection. A device
    /// controlled entirely through MusicHub/VideoHub/etc. (the common case)
    /// never touched it, and DeviceDropRuleCronJob's TTL/e-fuse rules read a
    /// permanently-null-or-stale value as abandoned and disowned it — while
    /// under continuous real use. Confirmed live, real TV, 2026-09-09.
    /// </summary>
    [Fact]
    public async Task OnConnectedAsync_WithClientId_AdvancesWsConnectedAt()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        QueryCollection query = new(
            new Dictionary<string, StringValues>
            {
                ["client_id"] = "device-lastseen",
                ["client_type"] = "tv",
            }
        );
        TestableConnectionHub hub = BuildHub(user, out _, out _, query: query);
        DateTime beforeConnect = DateTime.UtcNow;

        await hub.OnConnectedAsync();

        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        Device device = await ctx.Devices.SingleAsync(d => d.DeviceId == "device-lastseen");
        Assert.NotNull(device.WsConnectedAt);
        Assert.True(
            device.WsConnectedAt >= beforeConnect,
            "a hub connection must advance WsConnectedAt, or the drop-rule job reads this device as never seen"
        );
    }

    [Fact]
    public async Task OnConnectedAsync_WithoutClientId_DoesNotTouchDatabase_ButStillTracksClient()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        TestableConnectionHub hub = BuildHub(user, out _, out _);

        await hub.OnConnectedAsync();

        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        Assert.Equal(0, await ctx.Devices.CountAsync());
        Assert.Single(_connectedClients.Clients);
    }

    /// <summary>
    /// A tracked client no Devices row backs must not carry an id that looks like
    /// one. Every activity row written against such a client failed the foreign
    /// key and was dropped after three retries — one guest on a live server had
    /// not a single row recorded, ever. Empty is what the activity log reads as
    /// "no device", so the event lands with the device left blank.
    /// </summary>
    [Fact]
    public async Task OnConnectedAsync_WithoutClientId_TracksClientWithNoDeviceId()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        TestableConnectionHub hub = BuildHub(user, out _, out _);

        await hub.OnConnectedAsync();

        Client tracked = Assert.Single(_connectedClients.Clients).Value;
        Assert.Equal(Ulid.Empty, tracked.Id);
    }

    /// <summary>
    /// The same rule for a client that DID reach the upsert but whose lookup came
    /// back empty: the id it was constructed with still points at nothing.
    /// </summary>
    [Fact]
    public void ClearUnbackedDeviceId_LeavesNoIdToMistakeForAKey()
    {
        Client client = new();
        Assert.NotEqual(Ulid.Empty, client.Id);

        ConnectionHub.ClearUnbackedDeviceId(client);

        Assert.Equal(Ulid.Empty, client.Id);
    }

    [Fact]
    public async Task OnConnectedAsync_Reconnect_PreservesPersistedCustomNameAndVolume()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };

        await using (MediaContext seed = await _contextFactory.CreateDbContextAsync())
        {
            seed.Devices.Add(
                new()
                {
                    DeviceId = "device-reconnect",
                    Type = "tv",
                    CustomName = "Bedroom TV",
                    VolumePercent = 42,
                }
            );
            await seed.SaveChangesAsync();
        }

        QueryCollection query = new(
            new Dictionary<string, StringValues>
            {
                ["client_id"] = "device-reconnect",
                ["client_type"] = "tv",
                ["client_volume"] = "77", // must NOT clobber the persisted 42
            }
        );
        TestableConnectionHub hub = BuildHub(user, out _, out _, query: query);

        await hub.OnConnectedAsync();

        Client stored = _connectedClients.Clients.Values.Single();
        Assert.Equal("Bedroom TV", stored.CustomName);
        Assert.Equal(42, stored.VolumePercent);
    }

    [Fact]
    public async Task OnConnectedAsync_NonEmptyCustomName_OverwritesPersistedName()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        await using (MediaContext seed = await _contextFactory.CreateDbContextAsync())
        {
            seed.Devices.Add(
                new()
                {
                    DeviceId = "device-rename",
                    Type = "tv",
                    CustomName = "Old Name",
                }
            );
            await seed.SaveChangesAsync();
        }

        QueryCollection query = new(
            new Dictionary<string, StringValues>
            {
                ["client_id"] = "device-rename",
                ["client_type"] = "tv",
                ["custom_name"] = "New Name",
            }
        );
        TestableConnectionHub hub = BuildHub(user, out _, out _, query: query);

        await hub.OnConnectedAsync();

        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        Device device = await ctx.Devices.SingleAsync(d => d.DeviceId == "device-rename");
        Assert.Equal("New Name", device.CustomName);
    }

    [Fact]
    public async Task OnConnectedAsync_VolumePercentQueryParam_IsClampedTo0To100()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        QueryCollection query = new(
            new Dictionary<string, StringValues>
            {
                ["client_id"] = "device-clamp",
                ["client_type"] = "tv",
                ["client_volume"] = "150",
            }
        );
        TestableConnectionHub hub = BuildHub(user, out _, out _, query: query);

        await hub.OnConnectedAsync();

        // First-ever connection for this device — the upsert's INSERT path
        // (unlike WhenMatched on a reconnect) carries the whole in-memory
        // client, so the clamped value legitimately lands in the DB and
        // AlignClientWithPersistedDevice reads it straight back.
        Client stored = _connectedClients.Clients.Values.Single();
        Assert.Equal(100, stored.VolumePercent);
    }

    [Fact]
    public async Task OnConnectedAsync_NegativeVolumePercentQueryParam_ClampsToZero()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        QueryCollection query = new(
            new Dictionary<string, StringValues>
            {
                ["client_id"] = "device-clamp-negative",
                ["client_type"] = "tv",
                ["client_volume"] = "-20",
            }
        );
        TestableConnectionHub hub = BuildHub(user, out _, out _, query: query);

        await hub.OnConnectedAsync();

        Client stored = _connectedClients.Clients.Values.Single();
        Assert.Equal(0, stored.VolumePercent);
    }

    [Fact]
    public async Task OnConnectedAsync_SendsConnectedDevicesState_ToCallingUserOnly()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        TestableConnectionHub hub = BuildHub(
            user,
            out Mock<ISingleClientProxy> callerProxy,
            out Mock<ISingleClientProxy> userProxy
        );

        await hub.OnConnectedAsync();

        userProxy.Verify(
            p =>
                p.SendCoreAsync(
                    "ConnectedDevicesState",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        callerProxy.Verify(
            p =>
                p.SendCoreAsync(
                    "ConnectedDevicesState",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task OnDisconnectedAsync_UntrackedConnection_DoesNotThrow()
    {
        TestableConnectionHub hub = BuildHub(cachedUser: null, out _, out _);

        Exception? ex = await Record.ExceptionAsync(() => hub.OnDisconnectedAsync(null));

        Assert.Null(ex);
        Assert.Empty(_connectedClients.Clients);
    }

    [Fact]
    public async Task OnDisconnectedAsync_TrackedConnection_MarksDeviceInactive_AndRemovesFromMap()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        QueryCollection query = new(
            new Dictionary<string, StringValues>
            {
                ["client_id"] = "device-disconnect",
                ["client_type"] = "tv",
            }
        );
        TestableConnectionHub hub = BuildHub(user, out _, out _, query: query);
        await hub.OnConnectedAsync();
        Assert.Single(_connectedClients.Clients);

        await hub.OnDisconnectedAsync(null);

        Assert.Empty(_connectedClients.Clients);
        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        Device device = await ctx.Devices.SingleAsync(d => d.DeviceId == "device-disconnect");
        Assert.False(device.IsActive);
    }

    [Fact]
    public void Devices_UserNotInCache_ReturnsEmptyList()
    {
        TestableConnectionHub hub = BuildHub(
            cachedUser: null,
            out _,
            out _,
            connectingUserId: Guid.NewGuid()
        );

        List<Device> devices = hub.Devices();

        Assert.Empty(devices);
    }

    [Fact]
    public async Task Devices_OnlyReturnsCallingUsersOwnConnections_OnThisEndpoint()
    {
        User userA = new()
        {
            Id = Guid.NewGuid(),
            Email = "a@nomercy.tv",
            Name = "A",
        };
        User userB = new()
        {
            Id = Guid.NewGuid(),
            Email = "b@nomercy.tv",
            Name = "B",
        };

        // Simulate userB already connected on the same endpoint.
        _connectedClients.Clients.TryAdd(
            "other-connection",
            new()
            {
                Sub = userB.Id,
                Endpoint = "/videoHub",
                DeviceId = "device-b",
                Name = "B's device",
            }
        );

        TestableConnectionHub hub = BuildHub(userA, out _, out _, path: "/videoHub");
        await hub.OnConnectedAsync();

        List<Device> devices = hub.Devices();

        Assert.DoesNotContain(devices, d => d.DeviceId == "device-b");
    }

    [Fact]
    public async Task Devices_IgnoresConnectionsOnADifferentHubEndpoint()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };

        _connectedClients.Clients.TryAdd(
            "music-hub-connection",
            new()
            {
                Sub = user.Id,
                Endpoint = "/musicHub",
                DeviceId = "device-music",
                Name = "Music device",
            }
        );

        TestableConnectionHub hub = BuildHub(user, out _, out _, path: "/videoHub");
        await hub.OnConnectedAsync();

        List<Device> devices = hub.Devices();

        Assert.DoesNotContain(devices, d => d.DeviceId == "device-music");
    }

    [Fact]
    public void GetCountryFromContext_NoHeader_DefaultsToUs()
    {
        TestableConnectionHub hub = BuildHub(cachedUser: null, out _, out _);

        string country = hub.GetCountryFromContext();

        Assert.Equal("US", country);
    }

    [Fact]
    public void GetCountryFromContext_HeaderPresent_ReturnsHeaderValue()
    {
        UserCache userCache = new();
        ServiceCollection services = new();
        services.AddSingleton<IUserCache>(userCache);
        DefaultHttpContext httpContext = new()
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Path = "/videoHub";
        httpContext.Request.Headers["country"] = "NL";

        TestableConnectionHub hub = new(
            new HttpContextAccessorStub(httpContext),
            _contextFactory,
            _connectedClients,
            _activityLogger.Object
        );

        string country = hub.GetCountryFromContext();

        Assert.Equal("NL", country);
    }

    [Fact]
    public void GetLanguageFromContext_AcceptLanguageHeader_ReturnsPrimaryLanguageTag()
    {
        UserCache userCache = new();
        ServiceCollection services = new();
        services.AddSingleton<IUserCache>(userCache);
        DefaultHttpContext httpContext = new()
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Path = "/videoHub";
        httpContext.Request.Headers.AcceptLanguage = "nl_NL";

        TestableConnectionHub hub = new(
            new HttpContextAccessorStub(httpContext),
            _contextFactory,
            _connectedClients,
            _activityLogger.Object
        );

        string? language = hub.GetLanguageFromContext();

        Assert.Equal("nl", language);
    }

    [Fact]
    public void GetLanguageFromContext_NoHeader_FallsBackToGlobalLocalizerTargetLanguage()
    {
        TestableConnectionHub hub = BuildHub(cachedUser: null, out _, out _);

        string? language = hub.GetLanguageFromContext();

        Assert.NotNull(language);
    }

    [Fact]
    public void UserCacheService_WhenRequestServicesResolvesIt_UsesResolvedInstance()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        TestableConnectionHub hub = BuildHub(user, out _, out _);

        IUserCache resolved = hub.ExposedUserCacheService;

        Assert.NotNull(resolved.GetUser(user.Id));
    }

    [Fact]
    public void AuthPolicy_WhenRequestServicesResolvesNoPolicy_FallsBackToDefault()
    {
        TestableConnectionHub hub = BuildHub(cachedUser: null, out _, out _);

        IMediaAuthorizationPolicy policy = hub.ExposedAuthPolicy;

        Assert.NotNull(policy);
    }

    // -- HttpContext entirely absent (e.g. a hub method invoked outside a
    // request-bound connection). Every one of these accessors must fall back
    // to a safe default instead of NRE-ing on a null HttpContext.

    private TestableConnectionHub BuildHubWithNullHttpContext()
    {
        HttpContextAccessorStub accessor = new(null!);
        return new(accessor, _contextFactory, _connectedClients, _activityLogger.Object);
    }

    [Fact]
    public void GetCountryFromContext_NullHttpContext_DefaultsToUs()
    {
        TestableConnectionHub hub = BuildHubWithNullHttpContext();

        string country = hub.GetCountryFromContext();

        Assert.Equal("US", country);
    }

    [Fact]
    public void GetLanguageFromContext_NullHttpContext_FallsBackToGlobalLocalizer()
    {
        TestableConnectionHub hub = BuildHubWithNullHttpContext();

        string? language = hub.GetLanguageFromContext();

        Assert.NotNull(language);
    }

    [Fact]
    public void UserCacheService_NullHttpContext_FallsBackToUserCacheCurrent()
    {
        TestableConnectionHub hub = BuildHubWithNullHttpContext();

        IUserCache resolved = hub.ExposedUserCacheService;

        Assert.Same(UserCache.Current, resolved);
    }

    [Fact]
    public void AuthPolicy_NullHttpContext_FallsBackToDefault()
    {
        TestableConnectionHub hub = BuildHubWithNullHttpContext();

        IMediaAuthorizationPolicy policy = hub.ExposedAuthPolicy;

        Assert.NotNull(policy);
    }

    [Fact]
    public void UserCacheService_HttpContextPresent_ButRequestServicesNull_FallsBackToUserCacheCurrent()
    {
        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/videoHub";
        TestableConnectionHub hub = new(
            new HttpContextAccessorStub(httpContext),
            _contextFactory,
            _connectedClients,
            _activityLogger.Object
        );

        IUserCache resolved = hub.ExposedUserCacheService;

        Assert.Same(UserCache.Current, resolved);
    }

    [Fact]
    public void AuthPolicy_HttpContextPresent_ButRequestServicesNull_FallsBackToDefault()
    {
        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/videoHub";
        TestableConnectionHub hub = new(
            new HttpContextAccessorStub(httpContext),
            _contextFactory,
            _connectedClients,
            _activityLogger.Object
        );

        IMediaAuthorizationPolicy policy = hub.ExposedAuthPolicy;

        Assert.NotNull(policy);
    }

    [Fact]
    public async Task OnConnectedAsync_NullHttpContext_EndpointFallsBackToUnknown()
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "test@nomercy.tv",
            Name = "Test",
        };
        UserCache userCache = new();
        userCache.AddUser(user);

        HttpContextAccessorStub accessor = new(null!);
        TestableConnectionHub hub = new(
            accessor,
            _contextFactory,
            _connectedClients,
            _activityLogger.Object
        );

        Mock<HubCallerContext> context = new();
        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth")
        );
        context.Setup(c => c.User).Returns(principal);
        context.Setup(c => c.ConnectionId).Returns(Guid.NewGuid().ToString());
        context.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);
        hub.Context = context.Object;

        Mock<IHubCallerClients> clients = new();
        clients.Setup(c => c.Caller).Returns(Mock.Of<ISingleClientProxy>());
        clients.Setup(c => c.User(It.IsAny<string>())).Returns(Mock.Of<ISingleClientProxy>());
        hub.Clients = clients.Object;

        // UserCacheService falls back to the shared UserCache.Current when
        // HttpContext is null, so this test seeds the calling user there
        // instead of a per-test DI container. Reset afterward to keep this
        // static, process-wide cache from leaking into other tests.
        UserCache.Current.AddUser(user);
        try
        {
            await hub.OnConnectedAsync();
        }
        finally
        {
            UserCache.Current.RemoveUser(user);
        }

        Client stored = _connectedClients.Clients.Values.Single();
        Assert.Equal("Unknown", stored.Endpoint);
    }
}
