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
using NoMercy.NmSystem.Networking;
using NoMercy.Setup.Auth;
using Xunit;

namespace NoMercy.Tests.Setup.Auth;

[Trait("Category", "Unit")]
public sealed class CloudReachabilityProbeTests
{
    [Theory]
    [InlineData("reachable", ReachabilityVerdict.Reachable)]
    [InlineData("unreachable", ReachabilityVerdict.Unreachable)]
    [InlineData("unknown", ReachabilityVerdict.Unknown)]
    public void TheApiVerdict_MapsOntoTheThreeAnswers(string verdict, ReachabilityVerdict expected)
    {
        string json =
            $$$"""{"status":"ok","data":{"results":[{"url":"http://192.0.2.10:7626/x","verdict":"{{{verdict}}}","reachable":false}]}}""";

        Assert.Equal(expected, CloudReachabilityProbe.ParseVerdict(json));
    }

    [Theory]
    [InlineData("""{"status":"error"}""")]
    [InlineData("""{"status":"ok","data":{"results":[]}}""")]
    public void AnAnswerWithoutAResult_IsUnknown_NotClosed(string json)
    {
        Assert.Equal(ReachabilityVerdict.Unknown, CloudReachabilityProbe.ParseVerdict(json));
    }

    [Fact]
    public async Task TheClient_OnlyConnectsOverTheFamilyItWasMadeFor()
    {
        // The API calls back the address the request came from, so the request must leave
        // over exactly one family; "localhost" resolves to both.
        TcpListener v4 = new(IPAddress.Loopback, 0);
        v4.Start();
        int port = ((IPEndPoint)v4.LocalEndpoint).Port;
        try
        {
            using HttpClient client = CloudReachabilityProbe.CreateClient(AddressFamily.InterNetwork);
            Task<HttpResponseMessage> send = client.GetAsync($"http://localhost:{port}/");

            using TcpClient accepted = await v4.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                AddressFamily.InterNetwork,
                ((IPEndPoint)accepted.Client.RemoteEndPoint!).AddressFamily
            );
            accepted.Close();
            await Record.ExceptionAsync(() => send);
        }
        finally
        {
            v4.Stop();
        }
    }
}
