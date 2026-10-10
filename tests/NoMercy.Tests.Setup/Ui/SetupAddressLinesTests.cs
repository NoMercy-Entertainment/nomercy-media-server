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

using NoMercy.Setup.Ui;

namespace NoMercy.Tests.Setup.Ui;

/// <summary>
/// Requirement (issue #437): the setup address printed to the console and the log
/// leads with the LAN address, so an owner on a NAS can paste it on a laptop. The
/// localhost line follows, with its own label. With no LAN address only the localhost
/// line is printed (never <c>http://:port</c>). Inside a container whose only known
/// address is its own bridge address, a line tells the owner to use the NAS address
/// and the mapped host port.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SetupAddressLinesTests
{
    [Fact]
    public void LanIpKnown_PrintsNetworkLineThenLocalhostLine()
    {
        SetupAddress address = SetupAddress.Resolve("192.168.2.10", 7626, inContainer: false);

        Assert.Equal(
            [
                "Open on any device on your network: http://192.168.2.10:7626/setup",
                "On this machine: http://localhost:7626/setup",
            ],
            address.Lines
        );
        Assert.Equal("http://192.168.2.10:7626/setup", address.PreferredUrl);
        Assert.Equal("http://localhost:7626/setup", address.LocalhostUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    public void NoLanIp_PrintsOnlyTheLocalhostLine(string? lanIp)
    {
        SetupAddress address = SetupAddress.Resolve(lanIp, 7626, inContainer: false);

        Assert.Equal(["On this machine: http://localhost:7626/setup"], address.Lines);
        Assert.Null(address.NetworkUrl);
        Assert.Equal("http://localhost:7626/setup", address.PreferredUrl);
    }

    [Fact]
    public void ContainerWithBridgeAddress_AddsTheNasPortLine()
    {
        SetupAddress address = SetupAddress.Resolve("172.17.0.2", 7626, inContainer: true);

        Assert.Equal(
            [
                "Open on any device on your network: http://172.17.0.2:7626/setup",
                "On this machine: http://localhost:7626/setup",
                "If you mapped a different host port, use your NAS address and that port.",
            ],
            address.Lines
        );
    }

    [Fact]
    public void ContainerWithNoLanIp_AddsTheNasPortLine()
    {
        SetupAddress address = SetupAddress.Resolve("127.0.0.1", 7626, inContainer: true);

        Assert.Equal(
            [
                "On this machine: http://localhost:7626/setup",
                "If you mapped a different host port, use your NAS address and that port.",
            ],
            address.Lines
        );
    }

    [Fact]
    public void ContainerWithSuppliedHostIp_PrintsNoNasPortLine()
    {
        SetupAddress address = SetupAddress.Resolve("192.168.2.10", 7626, inContainer: true);

        Assert.Equal(2, address.Lines.Count);
        Assert.DoesNotContain(address.Lines, line => line.Contains("NAS"));
    }
}
