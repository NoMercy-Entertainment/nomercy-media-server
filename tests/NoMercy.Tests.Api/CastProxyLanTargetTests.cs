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

using System.Net;
using Microsoft.AspNetCore.Http;
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
            Ip = "203.0.113.49",
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
            Ip = "203.0.113.49",
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
            Ip = "203.0.113.49",
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
            Ip = "203.0.113.49",
            LanIp = "   ",
        };

        (string? host, _) = CastProxyController.ResolveLanTarget(tv);

        host.Should().BeNull();
    }

    // The LAN address comes from an unauthenticated mDNS answer. Pointed at this
    // machine, the proxy would call the loopback-only management API for any user.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.0.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("224.0.0.251")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("not-an-address")]
    public void IsDialableLanTarget_RefusesAddressesThatAreNotAnotherLanDevice(string address)
    {
        CastProxyController.IsDialableLanTarget(address, []).Should().BeFalse();
    }

    [Fact]
    public void IsDialableLanTarget_RefusesAnAddressOfThisServer()
    {
        CastProxyController
            .IsDialableLanTarget("192.168.2.201", [IPAddress.Parse("192.168.2.201")])
            .Should()
            .BeFalse();
    }

    // Defense in depth: if a forward still lands on this machine, LocalhostOnly sees a
    // relayed request and refuses it.
    [Fact]
    public void BuildForward_CarriesTheCallerAddress_SoLoopbackOnlyRoutesRefuseIt()
    {
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Headers.Authorization = "Bearer user-token";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.2.77");

        using HttpRequestMessage forward = CastProxyController.BuildForward(
            context,
            "http://192.168.2.21:7626/v1/launch"
        );

        forward.Method.Should().Be(HttpMethod.Post);
        forward.Headers.GetValues("X-Forwarded-For").Should().Equal("192.168.2.77");
        forward.Headers.GetValues("Authorization").Should().Equal("Bearer user-token");
    }

    [Theory]
    [InlineData("192.168.2.21")]
    [InlineData("10.0.0.40")]
    [InlineData("fd00::21")]
    public void IsDialableLanTarget_AllowsAnotherDeviceOnTheLan(string address)
    {
        CastProxyController
            .IsDialableLanTarget(address, [IPAddress.Parse("192.168.2.201")])
            .Should()
            .BeTrue();
    }
}
