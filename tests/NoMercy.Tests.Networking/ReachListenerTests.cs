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
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Connectivity;
using NoMercy.NmSystem.Networking;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed class ReachListenerTests
{
    private static CancellationTokenSource TestTimeout => new(TimeSpan.FromSeconds(5));

    // Loopback, so a test run never asks the firewall (see TestListenersBindLoopbackGuardTests).
    private static readonly IPAddress[] Loopbacks = [IPAddress.Loopback, IPAddress.IPv6Loopback];

    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<string> GetAsync(int port, string path)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, TestTimeout.Token);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 192.0.2.10:{port}\r\n\r\n"),
            TestTimeout.Token
        );
        using StreamReader reader = new(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync(TestTimeout.Token);
    }

    [Fact]
    public async Task BeforeTheServerIsUp_ALiveCodeIsAnsweredOverPlainHttp()
    {
        ReachCodes codes = new();
        string code = codes.Issue();
        int port = FreePort();

        await using ReachListener? listener = ReachListener.TryStart(
            port,
            codes,
            NullLogger.Instance,
            Loopbacks
        );

        Assert.NotNull(listener);
        string response = await GetAsync(port, ReachCodes.PathPrefix + code);
        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.EndsWith("\r\n\r\n" + code, response);
    }

    [Fact]
    public void ByDefault_ItListensWhereKestrelDoes()
    {
        // The call-back comes in from outside, on every family.
        Assert.Equal([IPAddress.Any, IPAddress.IPv6Any], ReachListener.AllAddresses);
    }

    [Fact]
    public async Task AnUnknownCode_IsNotAnswered()
    {
        ReachCodes codes = new();
        codes.Issue();
        int port = FreePort();

        await using ReachListener? listener = ReachListener.TryStart(
            port,
            codes,
            NullLogger.Instance,
            Loopbacks
        );

        string response = await GetAsync(port, ReachCodes.PathPrefix + "nope");
        Assert.StartsWith("HTTP/1.1 404", response);
    }

    [Fact]
    public async Task AfterDispose_ThePortIsFreeForKestrel()
    {
        int port = FreePort();
        ReachListener? listener = ReachListener.TryStart(port, new(), NullLogger.Instance, Loopbacks);
        Assert.NotNull(listener);

        await listener.DisposeAsync();

        TcpListener kestrelStandIn = new(IPAddress.Loopback, port);
        kestrelStandIn.Start();
        kestrelStandIn.Stop();
    }

    [Fact]
    public void APortSomethingElseHolds_GivesNoListener()
    {
        TcpListener v4 = new(IPAddress.Loopback, 0);
        v4.Start();
        int port = ((IPEndPoint)v4.LocalEndpoint).Port;
        TcpListener v6 = new(IPAddress.IPv6Loopback, port);
        v6.Server.DualMode = false;
        bool v6Bound = false;
        try
        {
            v6.Start();
            v6Bound = true;
        }
        catch (SocketException)
        {
            // No IPv6 on this host: holding the IPv4 port is enough.
        }

        try
        {
            ReachListener? listener = ReachListener.TryStart(port, new(), NullLogger.Instance, Loopbacks);

            Assert.Null(listener);
        }
        finally
        {
            v4.Stop();
            if (v6Bound)
                v6.Stop();
        }
    }
}
