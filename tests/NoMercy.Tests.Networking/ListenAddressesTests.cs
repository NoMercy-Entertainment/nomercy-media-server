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
        bool claimed = ListenAddresses.CanBindDualStack();

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
            probe.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            osBinds = true;
        }
        catch (SocketException)
        {
            osBinds = false;
        }

        Assert.Equal(osBinds, claimed);
    }

    [Fact]
    public async Task DualModeListener_ServesIPv4AndIPv6LoopbackOnOnePort()
    {
        if (!ListenAddresses.CanBindDualStack())
            return;

        using TcpListener listener = new(ListenAddresses.Wildcard(), 0);
        listener.Server.DualMode = true;
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using TcpClient v4 = new(AddressFamily.InterNetwork);
        await v4.ConnectAsync(IPAddress.Loopback, port);
        using TcpClient v6 = new(AddressFamily.InterNetworkV6);
        await v6.ConnectAsync(IPAddress.IPv6Loopback, port);

        using TcpClient fromV4 = await listener.AcceptTcpClientAsync();
        using TcpClient fromV6 = await listener.AcceptTcpClientAsync();

        // Existing IPv4 clients arrive as IPv4-mapped addresses; the resolver maps them back.
        IPAddress[] peers =
        [
            ((IPEndPoint)fromV4.Client.RemoteEndPoint!).Address,
            ((IPEndPoint)fromV6.Client.RemoteEndPoint!).Address,
        ];
        Assert.Contains(
            peers,
            p => p.IsIPv4MappedToIPv6 && p.MapToIPv4().Equals(IPAddress.Loopback)
        );
        Assert.Contains(peers, p => p.Equals(IPAddress.IPv6Loopback));
    }
}
