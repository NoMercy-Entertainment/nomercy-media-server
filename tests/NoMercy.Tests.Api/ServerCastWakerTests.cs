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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Services.Cast;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Cast;
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
}
