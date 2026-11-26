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

using NoMercy.Database.Models.Users;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Discovery;
using Xunit;

namespace NoMercy.Tests.Networking;

// A device row holds two addresses: LanIp, written by the mDNS scanner, is where the
// device sits on this network; Ip is where it last connected FROM, which for anyone
// watching through the tunnel is their public address. Every cast path read Ip, so a TV
// that had been watching from outside sent its LAUNCH at the owner's own router.
[Trait("Category", "Unit")]
public sealed class CastAddressTests
{
    [Fact]
    public void Resolve_PrefersTheLanAddressOverWhereTheDeviceConnectedFrom()
    {
        string? address = CastAddress.Resolve(lanIp: "192.168.2.31", connectedFromIp: "10.0.0.9");

        Assert.Equal("192.168.2.31", address);
    }

    [Fact]
    public void Resolve_TvSeenOnlyThroughTheTunnel_YieldsNoAddress()
    {
        string? address = CastAddress.Resolve(lanIp: null, connectedFromIp: "203.0.113.7");

        Assert.Null(address);
    }

    [Fact]
    public void Resolve_LanAddressWins_EvenWhenTheDeviceConnectedFromOutside()
    {
        string? address = CastAddress.Resolve(
            lanIp: "192.168.2.31",
            connectedFromIp: "203.0.113.7"
        );

        Assert.Equal("192.168.2.31", address);
    }

    [Fact]
    public void Resolve_NoLanSighting_FallsBackToAConnectionFromThisNetwork()
    {
        string? address = CastAddress.Resolve(lanIp: null, connectedFromIp: "192.168.2.31");

        Assert.Equal("192.168.2.31", address);
    }

    [Fact]
    public void Resolve_NothingRecorded_YieldsNoAddress()
    {
        Assert.Null(CastAddress.Resolve(null, null));
        Assert.Null(CastAddress.Resolve(string.Empty, string.Empty));
    }

    [Fact]
    public void Resolve_GarbageInEitherColumn_YieldsNoAddress()
    {
        Assert.Null(CastAddress.Resolve("not-an-ip", "also-not-an-ip"));
    }

    private static CastMdnsHit Hit(string id, string? friendlyName, string ip) =>
        new(id, friendlyName, "Chromecast", ip, DateTime.UtcNow);

    private static Device Tv(string name, string? customName, string? lanIp, string ip) =>
        new()
        {
            Id = Ulid.NewUlid(),
            Name = name,
            CustomName = customName,
            LanIp = lanIp,
            Ip = ip,
        };

    [Fact]
    public void ResolveWithSeen_NoAddressOnRow_UsesTheCastHitWithTheSameFriendlyName()
    {
        Device tv = Tv("Tv in woonkamer", null, null, "");

        string? address = CastAddress.ResolveForDevice(
            tv,
            [Hit("a", "Tv in woonkamer", "192.168.2.21"), Hit("b", "Bedroom", "192.168.2.80")]
        );

        Assert.Equal("192.168.2.21", address);
    }

    [Fact]
    public void ResolveWithSeen_CustomNameMatchesCaseAndPaddingInsensitively()
    {
        Device tv = Tv("Android TV", "  Living Room ", null, "203.0.113.7");

        string? address = CastAddress.ResolveForDevice(
            tv,
            [Hit("a", "living room", "192.168.2.21")]
        );

        Assert.Equal("192.168.2.21", address);
    }

    [Fact]
    public void ResolveWithSeen_TwoHitsShareTheName_YieldsNoAddress()
    {
        Device tv = Tv("Tv in woonkamer", null, null, "");

        string? address = CastAddress.ResolveForDevice(
            tv,
            [
                Hit("a", "Tv in woonkamer", "192.168.2.21"),
                Hit("b", "Tv in woonkamer", "192.168.2.22"),
            ]
        );

        Assert.Null(address);
    }

    [Fact]
    public void ResolveWithSeen_NoHitHasTheName_YieldsNoAddress()
    {
        Device tv = Tv("Tv in woonkamer", null, null, "");

        Assert.Null(CastAddress.ResolveForDevice(tv, [Hit("a", "Bedroom", "192.168.2.80")]));
    }

    [Fact]
    public void ResolveWithSeen_LanIpOnRow_WinsOverAMatchingHit()
    {
        Device tv = Tv("Tv in woonkamer", null, "192.168.2.31", "");

        string? address = CastAddress.ResolveForDevice(
            tv,
            [Hit("a", "Tv in woonkamer", "192.168.2.21")]
        );

        Assert.Equal("192.168.2.31", address);
    }

    [Fact]
    public void ResolveWithSeen_IpOnRowFromThisNetwork_WinsOverAMatchingHit()
    {
        Device tv = Tv("Tv in woonkamer", null, null, "192.168.2.40");

        string? address = CastAddress.ResolveForDevice(
            tv,
            [Hit("a", "Tv in woonkamer", "192.168.2.21")]
        );

        Assert.Equal("192.168.2.40", address);
    }

    [Fact]
    public void ResolveWithSeen_MatchingHitOnAPublicAddress_YieldsNoAddress()
    {
        Device tv = Tv("Tv in woonkamer", null, null, "");

        Assert.Null(CastAddress.ResolveForDevice(tv, [Hit("a", "Tv in woonkamer", "8.8.8.8")]));
    }

    [Theory]
    [InlineData("192.168.2.31", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.4", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("203.0.113.7", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("", false)]
    public void IsOnThisNetwork_SeparatesReachableFromRoutable(string ip, bool expected)
    {
        Assert.Equal(expected, CastAddress.IsOnThisNetwork(ip));
    }
}
