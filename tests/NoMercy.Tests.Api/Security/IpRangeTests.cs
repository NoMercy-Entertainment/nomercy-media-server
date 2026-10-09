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
using NoMercy.Api.Security;
using Xunit;

namespace NoMercy.Tests.Api.Security;

public class IpRangeTests
{
    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.0", true)]
    [InlineData("203.0.113.0/24", "203.0.113.255", true)]
    [InlineData("203.0.113.0/24", "203.0.114.0", false)]
    [InlineData("10.0.0.0/8", "10.255.255.255", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("100.64.0.0/10", "100.127.255.254", true)]
    [InlineData("100.64.0.0/10", "100.128.0.1", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    public void Contains_MatchesOnThePrefixOnly(string cidr, string address, bool expected)
    {
        IpRange.TryParse(cidr, out IpRange range).Should().BeTrue();

        range.Contains(IPAddress.Parse(address)).Should().Be(expected);
    }

    [Fact]
    public void TryParse_BareAddress_MatchesOnlyItself()
    {
        IpRange.TryParse("198.51.100.7", out IpRange range).Should().BeTrue();

        range.Contains(IPAddress.Parse("198.51.100.7")).Should().BeTrue();
        range.Contains(IPAddress.Parse("198.51.100.8")).Should().BeFalse();
    }

    [Fact]
    public void Contains_IsFalseAcrossAddressFamilies()
    {
        IpRange.TryParse("203.0.113.0/24", out IpRange range).Should().BeTrue();

        range.Contains(IPAddress.Parse("2a02::1")).Should().BeFalse();
    }

    [Fact]
    public void Contains_UnwrapsAnIpv4MappedIpv6Address()
    {
        IpRange.TryParse("203.0.113.0/24", out IpRange range).Should().BeTrue();

        range.Contains(IPAddress.Parse("203.0.113.9").MapToIPv6()).Should().BeTrue();
    }

    [Fact]
    public void TryParse_Ipv6Cidr_MatchesOnThePrefix()
    {
        IpRange.TryParse("2a02:1234::/32", out IpRange range).Should().BeTrue();

        range.Contains(IPAddress.Parse("2a02:1234:5678::1")).Should().BeTrue();
        range.Contains(IPAddress.Parse("2a02:1235::1")).Should().BeFalse();
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("203.0.113.0/")]
    [InlineData("203.0.113.0/64")]
    [InlineData("203.0.113.0/-1")]
    [InlineData("/24")]
    public void TryParse_RejectsAnythingItCannotUnderstand(string value)
    {
        IpRange.TryParse(value, out IpRange _).Should().BeFalse();
    }
}
