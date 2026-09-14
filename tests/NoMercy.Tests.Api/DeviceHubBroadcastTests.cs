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
using NoMercy.Api.Hubs;
using NoMercy.Api.WebSockets;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Encoder.Devices;
using NoMercy.Networking.Devices;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Messaging;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

// Regression coverage for the live two-TV verification finding: a TV connecting
// to /deviceHub only refreshed its OWN picker (Clients.Caller), so every other
// already-connected device's picker never learned about it until it happened to
// reconnect itself. The fix broadcasts "DeviceListChanged" to the whole user
// group instead. These tests build a real DeviceHub against the app's actual
// DI-configured MediaContext (via NoMercyApiFactory) and mock only the SignalR
// plumbing (HubCallerContext, IHubCallerClients) that a live connection would
// normally supply.
[Trait("Category", "Characterization")]
public class DeviceHubBroadcastTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public DeviceHubBroadcastTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        // Force the test host to start so its root service provider is populated.
        _factory.CreateClient();
    }

    private async Task<(Ulid deviceA, Ulid deviceB)> SeedTwoOwnedDevicesAsync(
        IDbContextFactory<MediaContext> contextFactory
    )
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();

        Device deviceA = new()
        {
            Id = Ulid.NewUlid(),
            DeviceId = $"tv-a-{Guid.NewGuid()}",
            Name = "Living Room TV",
            Type = "tv",
            Fingerprint = $"fp-a-{Guid.NewGuid()}",
            OwnerUserId = TestAuthHandler.DefaultUserId,
        };
        Device deviceB = new()
        {
            Id = Ulid.NewUlid(),
            DeviceId = $"tv-b-{Guid.NewGuid()}",
            Name = "Bedroom TV",
            Type = "tv",
            Fingerprint = $"fp-b-{Guid.NewGuid()}",
            OwnerUserId = TestAuthHandler.DefaultUserId,
        };
        ctx.Devices.AddRange(deviceA, deviceB);
        await ctx.SaveChangesAsync();

        return (deviceA.Id, deviceB.Id);
    }

    private DeviceHub CreateHub(
        out Mock<ISingleClientProxy> callerProxy,
        out Mock<ISingleClientProxy> userProxy
    )
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        ConnectedClients connectedClients = _factory.GetConnectedClients();
        DeviceBusRegistry busRegistry = new(
            new DeviceStateRepository(contextFactory),
            Mock.Of<IHubContext<DeviceHub>>(),
            Mock.Of<ICastMdnsRegistry>()
        );

        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/deviceHub";

        DeviceHub hub = new(
            new HttpContextAccessorStub(httpContext),
            contextFactory,
            connectedClients,
            busRegistry,
            new DeviceStateRepository(contextFactory),
            Mock.Of<Database.Activity.IActivityLogger>(),
            Mock.Of<IDeviceCapabilityRegistry>(),
            Mock.Of<ICastMdnsRegistry>(),
            Mock.Of<NoMercy.Api.Services.Cast.IServerCastWaker>(),
            NullLogger<DeviceHub>.Instance
        );

        ClaimsPrincipal principal = new(
            new ClaimsIdentity(
                [new(ClaimTypes.NameIdentifier, TestAuthHandler.DefaultUserId.ToString())],
                "TestAuth"
            )
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
    public async Task OnConnectedAsync_BroadcastsDeviceListChanged_ToWholeUserGroup_NotJustCaller()
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        await SeedTwoOwnedDevicesAsync(contextFactory);

        DeviceHub hub = CreateHub(
            out Mock<ISingleClientProxy> callerProxy,
            out Mock<ISingleClientProxy> userProxy
        );

        await hub.OnConnectedAsync();

        // The whole user group must receive the refreshed list...
        userProxy.Verify(
            p =>
                p.SendCoreAsync(
                    "DeviceListChanged",
                    It.Is<object?[]>(args => args.Length == 1 && args[0] is List<DeviceListItem>),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // ...never just the connecting client. This is the regression: the old
        // code sent "DeviceListChanged" only to Clients.Caller, so every other
        // already-connected device's picker stayed stale.
        callerProxy.Verify(
            p =>
                p.SendCoreAsync(
                    "DeviceListChanged",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task OnConnectedAsync_DeviceListChanged_ContainsBothOwnedDevices()
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        (Ulid deviceA, Ulid deviceB) = await SeedTwoOwnedDevicesAsync(contextFactory);

        DeviceHub hub = CreateHub(out _, out Mock<ISingleClientProxy> userProxy);

        List<DeviceListItem>? broadcastList = null;
        userProxy
            .Setup(p =>
                p.SendCoreAsync(
                    "DeviceListChanged",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<string, object?[], CancellationToken>(
                (_, args, _) => broadcastList = args[0] as List<DeviceListItem>
            )
            .Returns(Task.CompletedTask);

        await hub.OnConnectedAsync();

        broadcastList.Should().NotBeNull();
        broadcastList!.Select(d => d.DeviceId).Should().Contain([deviceA, deviceB]);
    }

    [Fact]
    public async Task OnDisconnectedAsync_BroadcastsDeviceListChanged_ToWholeUserGroup()
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        await SeedTwoOwnedDevicesAsync(contextFactory);

        DeviceHub hub = CreateHub(
            out Mock<ISingleClientProxy> callerProxy,
            out Mock<ISingleClientProxy> userProxy
        );

        await hub.OnDisconnectedAsync(null);

        userProxy.Verify(
            p =>
                p.SendCoreAsync(
                    "DeviceListChanged",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        callerProxy.Verify(
            p =>
                p.SendCoreAsync(
                    "DeviceListChanged",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    // Minimal IHttpContextAccessor stand-in — the real implementation is an
    // AsyncLocal-backed singleton unsuited to constructing an isolated
    // HttpContext per test.
    private sealed class HttpContextAccessorStub(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}
