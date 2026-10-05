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

using NoMercy.Api.Controllers.V1;
using NoMercy.Database.Models.Users;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Which address the cast proxy dials.
///
/// Casting from the web did nothing at all because the proxy forwarded to
/// <see cref="Device.Ip" /> — the address the TV was last seen from, which through
/// the tunnel is the household's public IP. Ten seconds of dialling an address that
/// routes nowhere from inside the LAN, then a gateway timeout the browser reports as
/// a failed fetch. Only the mDNS-advertised address reaches a TV's control server.
/// </summary>
[Trait("Category", "Unit")]
public class CastProxyLanTargetTests
{
    private const int DefaultControlPort = 7626;

    [Fact]
    public void ResolveLanTarget_PrefersTheLanAddress_OverTheAddressTheDeviceWasSeenFrom()
    {
        Device tv = new()
        {
            Name = "Tv in woonkamer",
            Ip = "85.144.244.49",
            LanIp = "192.168.2.21",
        };

        (string? host, int port) = CastProxyController.ResolveLanTarget(tv);

        host.Should().Be("192.168.2.21");
        port.Should().Be(DefaultControlPort);
    }

    [Fact]
    public void ResolveLanTarget_UsesTheAdvertisedPort_WhenTheDeviceReportsOne()
    {
        Device tv = new()
        {
            Name = "Bedroom TV",
            Ip = "85.144.244.49",
            LanIp = "192.168.2.44",
            LanPort = 8626,
        };

        (string? host, int port) = CastProxyController.ResolveLanTarget(tv);

        host.Should().Be("192.168.2.44");
        port.Should().Be(8626);
    }

    /// <summary>
    /// A device with no LAN address is unreachable and must be reported as such.
    /// Falling back to the public address is what produced a ten-second timeout
    /// instead of an immediate, honest failure.
    /// </summary>
    [Fact]
    public void ResolveLanTarget_WithoutALanAddress_IsUnreachableRatherThanPublic()
    {
        Device tv = new()
        {
            Name = "Sleeping TV",
            Ip = "85.144.244.49",
            LanIp = null,
        };

        (string? host, _) = CastProxyController.ResolveLanTarget(tv);

        host.Should().BeNull();
    }

    [Fact]
    public void ResolveLanTarget_TreatsABlankLanAddressAsAbsent()
    {
        Device tv = new()
        {
            Name = "Half-registered TV",
            Ip = "85.144.244.49",
            LanIp = "   ",
        };

        (string? host, _) = CastProxyController.ResolveLanTarget(tv);

        host.Should().BeNull();
    }
}
