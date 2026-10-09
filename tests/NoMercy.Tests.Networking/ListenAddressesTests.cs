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
using System.Net.Sockets;
using NoMercy.Networking.Http;
using Xunit;

namespace NoMercy.Tests.Networking;

public sealed class ListenAddressesTests
{
    [Fact]
    public void Wildcard_WhenDualStackBinds_IsIPv6Any()
    {
        Assert.Equal(IPAddress.IPv6Any, ListenAddresses.Wildcard(() => true));
    }

    [Fact]
    public void Wildcard_WhenDualStackCannotBind_FallsBackToIPv4Any()
    {
        Assert.Equal(IPAddress.Any, ListenAddresses.Wildcard(() => false));
    }

    [Fact]
    public void CanBindDualStack_AgreesWithTheOperatingSystem()
    {
        // The chooser must never claim dual-stack on a host that cannot bind "::".
        // The test probes "::1": a wildcard bind makes Windows ask the firewall
        // on every run, and a loopback dual-mode bind fails for the same reasons.
        bool claimed = ListenAddresses.CanBindDualStack(IPAddress.IPv6Loopback);

        if (!Socket.OSSupportsIPv6)
        {
            Assert.False(claimed);
            return;
        }

        using Socket probe = new(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        probe.DualMode = true;
        bool osBinds;
        try
        {
            probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            osBinds = true;
        }
        catch (SocketException)
        {
            osBinds = false;
        }

        Assert.Equal(osBinds, claimed);
    }

    [Fact]
    public async Task DualModeListener_OnLoopback_ServesIPv6AndMapsIPv4Peers()
    {
        // Decision: the IPv4 half of a dual-mode listener can only be proven on a
        // wildcard bind, and a wildcard bind asks the Windows firewall on every
        // test run. So the socket half runs on "::1" and the IPv4-mapped half is
        // proven on the address math the resolver relies on.
        if (!ListenAddresses.CanBindDualStack(IPAddress.IPv6Loopback))
            return;

        using TcpListener listener = new(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = true;
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using TcpClient v6 = new(AddressFamily.InterNetworkV6);
        await v6.ConnectAsync(IPAddress.IPv6Loopback, port);
        using TcpClient fromV6 = await listener.AcceptTcpClientAsync();

        Assert.Equal(IPAddress.IPv6Loopback, ((IPEndPoint)fromV6.Client.RemoteEndPoint!).Address);

        // Existing IPv4 clients arrive as IPv4-mapped addresses; the resolver maps them back.
        IPAddress mapped = IPAddress.Loopback.MapToIPv6();
        Assert.True(mapped.IsIPv4MappedToIPv6);
        Assert.Equal(IPAddress.Loopback, mapped.MapToIPv4());
    }
}
