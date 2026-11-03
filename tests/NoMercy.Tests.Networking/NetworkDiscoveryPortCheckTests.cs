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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Status;
using NoMercy.Storage.Drivers.Local;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: IsPortOpenAsync is the port-forward decision primitive —
/// PortForwardStrategy trusts its result to decide whether the server is
/// reachable from outside the LAN. It must refuse to probe an address that is
/// not a known external one (the 0.0.0.0 sentinel, empty, loopback), and its
/// connect step (CanConnectAsync) must return true against a real listening
/// endpoint, false against a closed port, and false (not throw) on a
/// connection timeout. These use real loopback TCP sockets — no mock of the
/// method under test.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NetworkDiscoveryPortCheckTests
{
    private static NetworkDiscovery BuildDiscovery(string externalIp, int externalPort)
    {
        NetworkDiscovery discovery = new(
            NullLogger<NetworkDiscovery>.Instance,
            new LocalStorageDriver(),
            new AuthTokenStore(),
            new ConnectivityStatus(),
            new()
        );
        discovery.ExternalIp = externalIp;
        RuntimeServerSettings.Current.ExternalServerPort = externalPort;
        return discovery;
    }

    [Fact]
    public async Task CanConnectAsync_AgainstRealListener_ReturnsTrue()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int originalPort = RuntimeServerSettings.Current.ExternalServerPort;

        try
        {
            _ = Task.Run(async () =>
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync();
            });

            NetworkDiscovery discovery = BuildDiscovery("127.0.0.1", port);

            bool result = await discovery.CanConnectAsync("127.0.0.1", port);

            Assert.True(result);
        }
        finally
        {
            listener.Stop();
            RuntimeServerSettings.Current.ExternalServerPort = originalPort;
        }
    }

    [Fact]
    public async Task CanConnectAsync_AgainstClosedPort_ReturnsFalse()
    {
        // Bind, note the free port, then close it immediately — nothing is
        // listening there, so the connect attempt gets a real RST/refused.
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int freePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        int originalPort = RuntimeServerSettings.Current.ExternalServerPort;

        try
        {
            NetworkDiscovery discovery = BuildDiscovery("127.0.0.1", freePort);

            bool result = await discovery.CanConnectAsync("127.0.0.1", freePort);

            Assert.False(result);
        }
        finally
        {
            RuntimeServerSettings.Current.ExternalServerPort = originalPort;
        }
    }

    [Fact]
    public async Task CanConnectAsync_AgainstNonRoutableAddress_TimesOutAndReturnsFalse()
    {
        // TEST-NET-1 (RFC 5737, 192.0.2.0/24) is reserved for documentation —
        // guaranteed unreachable without hitting a live host, exercising the
        // OperationCanceledException timeout branch deterministically.
        int originalPort = RuntimeServerSettings.Current.ExternalServerPort;

        try
        {
            NetworkDiscovery discovery = BuildDiscovery("192.0.2.1", 9);

            bool result = await discovery.CanConnectAsync("192.0.2.1", 9);

            Assert.False(result);
        }
        finally
        {
            RuntimeServerSettings.Current.ExternalServerPort = originalPort;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("not-an-address")]
    public void IsRoutableExternalAddress_UnknownOrLocalAddress_IsNotRoutable(string? address)
    {
        Assert.False(NetworkDiscovery.IsRoutableExternalAddress(address));
    }

    [Theory]
    [InlineData("203.0.113.42")]
    [InlineData("2001:db8::1")]
    public void IsRoutableExternalAddress_PublicAddress_IsRoutable(string address)
    {
        Assert.True(NetworkDiscovery.IsRoutableExternalAddress(address));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("127.0.0.1")]
    public async Task IsPortOpenAsync_WithNoKnownExternalAddress_ReturnsFalseWithoutConnecting(
        string? externalIp
    )
    {
        // ExternalIp falls back to 0.0.0.0 when every lookup failed. On Linux a TCP connect
        // to 0.0.0.0 — and on every platform one to loopback — lands on this machine, where
        // the server itself listens, so the probe "proved" a port forward it never tested
        // and PortForwardStrategy reported the server reachable from outside (#202). The
        // listener below plays the server's own port: a probe that refuses to test an
        // unknown address leaves it with nothing to accept.
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int originalPort = RuntimeServerSettings.Current.ExternalServerPort;

        try
        {
            NetworkDiscovery discovery = BuildDiscovery(externalIp!, port);

            bool result = await discovery.IsPortOpenAsync();

            Assert.False(result);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
            RuntimeServerSettings.Current.ExternalServerPort = originalPort;
        }
    }
}
