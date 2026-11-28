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

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Api.Services.Cast;
using NoMercy.Api.WebSockets;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Messaging;
using NoMercy.Setup.Cast;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Unit")]
public class ServerCastWakerTests
{
    private const string TvIp = "192.168.1.50";

    private static (ServerCastWaker Waker, Mock<IChromeCastService> ChromeCast) MakeWaker()
    {
        Mock<IChromeCastService> chromeCast = new();
        CastPanelWakeLauncher launcher = new(
            chromeCast.Object,
            NullLogger<CastPanelWakeLauncher>.Instance
        );
        ServerCastWaker waker = new(
            launcher,
            new CastSessionTokenService(),
            new HttpContextAccessor(),
            NullLogger<ServerCastWaker>.Instance
        );
        return (waker, chromeCast);
    }

    private static Device Tv() =>
        new()
        {
            Id = Ulid.NewUlid(),
            Name = "Test Room TV",
            Type = "tv",
            LanIp = TvIp,
        };

    [Fact]
    public async Task WakeAsync_NoReceiverAtTheAddress_ReturnsFalse()
    {
        (ServerCastWaker waker, Mock<IChromeCastService> chromeCast) = MakeWaker();
        chromeCast.Setup(c => c.FindReceiverNameByIpAsync(TvIp)).ReturnsAsync((string?)null);

        bool dispatched = await waker.WakeAsync(Tv(), Guid.NewGuid(), CastIntent.Idle(), null);

        dispatched.Should().BeFalse();
    }

    [Fact]
    public async Task WakeAsync_LaunchThrows_ReturnsFalse()
    {
        (ServerCastWaker waker, Mock<IChromeCastService> chromeCast) = MakeWaker();
        chromeCast.Setup(c => c.FindReceiverNameByIpAsync(TvIp)).ReturnsAsync("Test Room TV");
        chromeCast
            .Setup(c => c.SelectChromecast("Test Room TV"))
            .ThrowsAsync(new InvalidOperationException("receiver refused"));

        bool dispatched = await waker.WakeAsync(Tv(), Guid.NewGuid(), CastIntent.Idle(), null);

        dispatched.Should().BeFalse();
    }

    [Fact]
    public async Task WakeAsync_ReceiverLaunched_ReturnsTrue()
    {
        (ServerCastWaker waker, Mock<IChromeCastService> chromeCast) = MakeWaker();
        chromeCast.Setup(c => c.FindReceiverNameByIpAsync(TvIp)).ReturnsAsync("Test Room TV");

        bool dispatched = await waker.WakeAsync(Tv(), Guid.NewGuid(), CastIntent.Idle(), null);

        dispatched.Should().BeTrue();
        chromeCast.Verify(
            c => c.LaunchAndroidReceiver("Test Room TV", It.IsAny<object?>(), It.IsAny<bool>()),
            Times.Once
        );
    }

    private static (
        ServerCastWaker Waker,
        Mock<IChromeCastService> ChromeCast,
        ConnectedClients Clients
    ) MakeWakerWithHubs()
    {
        Mock<IChromeCastService> chromeCast = new();
        chromeCast.Setup(c => c.FindReceiverNameByIpAsync(TvIp)).ReturnsAsync("Test Room TV");
        ConnectedClients clients = new();
        DeviceBusRegistry busRegistry = new(
            Mock.Of<IDeviceStateRepository>(),
            Mock.Of<IHubContext<DeviceHub>>(),
            Mock.Of<ICastMdnsRegistry>(),
            clients
        );
        ServerCastWaker waker = new(
            new CastPanelWakeLauncher(
                chromeCast.Object,
                NullLogger<CastPanelWakeLauncher>.Instance,
                followUpPollIntervalMs: 1,
                followUpPollAttempts: 2
            ),
            new CastSessionTokenService(),
            new HttpContextAccessor(),
            NullLogger<ServerCastWaker>.Instance,
            busRegistry: busRegistry
        );
        return (waker, chromeCast, clients);
    }

    private static Device RegisteredTv() =>
        new()
        {
            Id = Ulid.NewUlid(),
            DeviceId = "test-device-0001",
            Fingerprint = "test-fingerprint-0001",
            Name = "Test Room TV",
            Type = "tv",
            LanIp = TvIp,
        };

    [Fact]
    public async Task WakeAsync_AppConnectedOnAHubButNotTheBus_NeverFallsBackToTheWebReceiver()
    {
        (ServerCastWaker waker, Mock<IChromeCastService> chromeCast, ConnectedClients clients) =
            MakeWakerWithHubs();
        Device tv = RegisteredTv();
        clients.Clients["connection-1"] = new()
        {
            DeviceId = tv.DeviceId,
            Endpoint = "/videoHub",
        };

        await waker.WakeAsync(tv, Guid.NewGuid(), CastIntent.Idle(), null);
        await Task.Delay(300);

        chromeCast.Verify(
            c => c.LaunchAndroidReceiver("Test Room TV", It.IsAny<object?>(), true),
            Times.Once
        );
        chromeCast.Verify(
            c => c.LaunchAndroidReceiver("Test Room TV", It.IsAny<object?>(), false),
            Times.Never
        );
    }

    [Fact]
    public async Task WakeAsync_AppNeverConnects_FallsBackToTheWebReceiver()
    {
        (ServerCastWaker waker, Mock<IChromeCastService> chromeCast, _) = MakeWakerWithHubs();

        await waker.WakeAsync(RegisteredTv(), Guid.NewGuid(), CastIntent.Idle(), null);
        await Task.Delay(300);

        chromeCast.Verify(
            c => c.LaunchAndroidReceiver("Test Room TV", It.IsAny<object?>(), false),
            Times.Once
        );
    }

    // A TV the NoMercy app never announced (no LanIp, no Ip) is found only by its Cast
    // name. It has no NoMercy app, so the Web Receiver starts at once, not after the
    // Cast Connect timeout.
    [Fact]
    public async Task WakeAsync_TvFoundOnlyByCastName_LaunchesTheWebReceiverAtOnce()
    {
        Mock<IChromeCastService> chromeCast = new();
        Mock<ICastMdnsRegistry> mdns = new();
        mdns.Setup(m => m.GetSeen())
            .Returns([
                new CastMdnsHit("cast-1", "Test Room TV", "Chromecast", TvIp, DateTime.UtcNow),
            ]);
        DeviceBusRegistry busRegistry = new(
            Mock.Of<IDeviceStateRepository>(),
            Mock.Of<IHubContext<DeviceHub>>(),
            mdns.Object
        );
        ServerCastWaker waker = new(
            new CastPanelWakeLauncher(
                chromeCast.Object,
                NullLogger<CastPanelWakeLauncher>.Instance
            ),
            new CastSessionTokenService(),
            new HttpContextAccessor(),
            NullLogger<ServerCastWaker>.Instance,
            busRegistry: busRegistry
        );
        chromeCast.Setup(c => c.FindReceiverNameByIpAsync(TvIp)).ReturnsAsync("Test Room TV");
        Device tv = new()
        {
            Id = Ulid.NewUlid(),
            Name = "Test Room TV",
            Type = "tv",
        };

        bool dispatched = await waker.WakeAsync(tv, Guid.NewGuid(), CastIntent.Idle(), null);

        dispatched.Should().BeTrue();
        chromeCast.Verify(
            c => c.LaunchAndroidReceiver("Test Room TV", It.IsAny<object?>(), false),
            Times.Once
        );
        chromeCast.Verify(
            c => c.LaunchAndroidReceiver("Test Room TV", It.IsAny<object?>(), true),
            Times.Never
        );
    }
}
