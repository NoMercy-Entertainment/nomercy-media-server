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
using NoMercy.Networking.Http;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: every external request that reaches this server through a local
/// relay (Cloudflare Tunnel, nginx, a container port mapping) arrives from
/// 127.0.0.1. Without resolving the forwarded caller, every access-log line,
/// every registered device and every auth failure records the relay instead of
/// the caller — an abusive IP can then never be identified or banned. The
/// forwarded caller must only be believed when the peer is a relay we could own,
/// so a directly-connected client can never dictate the address we log or ban.
/// </summary>
[Trait("Category", "Unit")]
public class ClientIpResolverTests
{
    private static DefaultHttpContext ContextFrom(string peer, params (string, string)[] headers)
    {
        DefaultHttpContext context = new()
        {
            Connection = { RemoteIpAddress = IPAddress.Parse(peer) },
        };

        foreach ((string name, string value) in headers)
            context.Request.Headers[name] = value;

        return context;
    }

    [Fact]
    public void ClientIp_ReturnsPeer_WhenNotProxied()
    {
        DefaultHttpContext context = ContextFrom("81.171.28.44");

        context.ClientIp().Should().Be(IPAddress.Parse("81.171.28.44"));
    }

    [Fact]
    public void ClientIp_UsesCloudflareHeader_WhenRelayIsLoopback()
    {
        DefaultHttpContext context = ContextFrom(
            "127.0.0.1",
            ("CF-Connecting-IP", "45.148.10.99"),
            ("X-Forwarded-For", "45.148.10.99, 172.71.30.5")
        );

        context.ClientIp().Should().Be(IPAddress.Parse("45.148.10.99"));
    }

    [Fact]
    public void ClientIp_TakesRightmostUntrustedHop_FromForwardedChain()
    {
        DefaultHttpContext context = ContextFrom(
            "127.0.0.1",
            ("X-Forwarded-For", "10.0.0.9, 45.148.10.99, 192.168.1.2")
        );

        context.ClientIp().Should().Be(IPAddress.Parse("45.148.10.99"));
    }

    [Fact]
    public void ClientIp_StripsPortFromForwardedEntry()
    {
        DefaultHttpContext context = ContextFrom(
            "127.0.0.1",
            ("X-Forwarded-For", "45.148.10.99:51820")
        );

        context.ClientIp().Should().Be(IPAddress.Parse("45.148.10.99"));
    }

    [Fact]
    public void ClientIp_FallsBackToRealIpHeader()
    {
        DefaultHttpContext context = ContextFrom("192.168.2.60", ("X-Real-IP", "45.148.10.99"));

        context.ClientIp().Should().Be(IPAddress.Parse("45.148.10.99"));
    }

    [Fact]
    public void ClientIp_IgnoresForwardedHeaders_FromUntrustedPeer()
    {
        DefaultHttpContext context = ContextFrom(
            "45.148.10.99",
            ("CF-Connecting-IP", "127.0.0.1"),
            ("X-Forwarded-For", "127.0.0.1")
        );

        context.ClientIp().Should().Be(IPAddress.Parse("45.148.10.99"));
    }

    [Fact]
    public void ClientIp_ReturnsRelay_WhenChainIsAllPrivate()
    {
        DefaultHttpContext context = ContextFrom("127.0.0.1", ("X-Forwarded-For", "10.0.0.9"));

        context.ClientIp().Should().Be(IPAddress.Parse("10.0.0.9"));
    }

    [Fact]
    public void ClientIp_UnwrapsIPv4MappedPeer()
    {
        DefaultHttpContext context = ContextFrom("::ffff:45.148.10.99");

        context.ClientIp().Should().Be(IPAddress.Parse("45.148.10.99"));
    }

    [Fact]
    public void IsProxied_IsTrue_ForLoopbackPeerCarryingForwardedHeader()
    {
        DefaultHttpContext context = ContextFrom("127.0.0.1", ("CF-Connecting-IP", "45.148.10.99"));

        context.IsProxied().Should().BeTrue();
    }

    [Fact]
    public void IsProxied_IsFalse_ForRealLocalCaller()
    {
        DefaultHttpContext context = ContextFrom("127.0.0.1");

        context.IsProxied().Should().BeFalse();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.4.4.4")]
    [InlineData("192.168.1.50")]
    [InlineData("172.20.0.9")]
    [InlineData("100.100.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public void IsPrivateNetwork_TrueForRangesOnlyAnOperatorControls(string address)
    {
        ClientIpResolver.IsPrivateNetwork(IPAddress.Parse(address)).Should().BeTrue();
    }

    [Theory]
    [InlineData("203.0.113.77")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2a02::1")]
    public void IsPrivateNetwork_FalseForPublicAddresses(string address)
    {
        ClientIpResolver.IsPrivateNetwork(IPAddress.Parse(address)).Should().BeFalse();
    }

    [Fact]
    public void IsPrivateNetwork_FalseForNoAddressAtAll()
    {
        ClientIpResolver.IsPrivateNetwork(null).Should().BeFalse();
    }
}
