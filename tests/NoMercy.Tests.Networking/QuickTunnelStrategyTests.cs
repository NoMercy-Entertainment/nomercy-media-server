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

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Connectivity;
using NoMercy.Networking.Connectivity.Strategies;
using NoMercy.NmSystem.Status;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed class QuickTunnelStrategyTests
{
    [Fact]
    public void AssignedUrlIn_FindsTheNameInsideCloudflaredsBanner()
    {
        const string line =
            "2026-09-15T00:00:00Z INF |  https://greeting-pension-adds-patio.trycloudflare.com                                     |";

        Assert.Equal(
            "https://greeting-pension-adds-patio.trycloudflare.com",
            QuickTunnelStrategy.AssignedUrlIn(line)
        );
    }

    [Fact]
    public void AssignedUrlIn_IgnoresLinesWithoutAName()
    {
        Assert.Null(QuickTunnelStrategy.AssignedUrlIn("INF Settings: map[protocol:http2]"));
        Assert.Null(
            QuickTunnelStrategy.AssignedUrlIn("INF visit https://developers.cloudflare.com")
        );
    }

    [Fact]
    public void AssignedUrlIn_NeverAcceptsANameOutsideTrycloudflare()
    {
        // A log line quoting some other host must not become the advertised address.
        Assert.Null(
            QuickTunnelStrategy.AssignedUrlIn("https://evil.example.com/trycloudflare.com")
        );
    }

    [Fact]
    public void IsConnectionRegistered_MatchesBothWordings()
    {
        Assert.True(
            QuickTunnelStrategy.IsConnectionRegistered(
                "INF Registered tunnel connection connIndex=0 connection=e051694c location=ams17 protocol=http2"
            )
        );
        Assert.True(
            QuickTunnelStrategy.IsConnectionRegistered("INF Connection 9c1e2b7a-1 registered")
        );
        Assert.False(QuickTunnelStrategy.IsConnectionRegistered("INF Starting tunnel"));
    }

    [Fact]
    public void Priority_SitsBelowEveryOtherTransport()
    {
        QuickTunnelStrategy strategy = new(
            NullLogger<QuickTunnelStrategy>.Instance,
            new ConnectivityStatus(),
            binaryExists: () => true
        );

        // The floor of the ladder: it only runs when nothing above it could be verified.
        Assert.True(strategy.Priority > 3);
        Assert.Equal(ConnectivityType.QuickTunnel, strategy.Type);
    }

    [Fact]
    public async Task TryEstablishAsync_WithoutTheBinary_FailsInsteadOfThrowing()
    {
        QuickTunnelStrategy strategy = new(
            NullLogger<QuickTunnelStrategy>.Instance,
            new ConnectivityStatus(),
            binaryExists: () => false
        );

        Assert.False(strategy.IsReady);
        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);
        Assert.False(result.Established);
    }

    [Fact]
    public void IsPublishedAnswer_NeedsAnARecord_NotJustStatusZero()
    {
        // The exact shape cloudflare-dns.com returns while the name does not exist yet.
        const string notYet =
            """{"Status":3,"TC":false,"RD":true,"RA":true,"AD":false,"CD":false,"Question":[{"name":"x.trycloudflare.com","type":1}],"Authority":[{"name":"trycloudflare.com","type":6,"TTL":1800,"data":"kevin.ns.cloudflare.com. dns.cloudflare.com. 2414927406 10000 2400 604800 1800"}]}""";
        const string published =
            """{"Status":0,"Question":[{"name":"x.trycloudflare.com","type":1}],"Answer":[{"name":"x.trycloudflare.com","type":1,"TTL":300,"data":"104.16.231.132"}]}""";
        const string existsWithoutAddress =
            """{"Status":0,"Question":[{"name":"x.trycloudflare.com","type":1}]}""";
        const string onlyACname =
            """{"Status":0,"Answer":[{"name":"x.trycloudflare.com","type":5,"TTL":300,"data":"edge.example."}]}""";

        Assert.False(QuickTunnelStrategy.IsPublishedAnswer(JsonDocument.Parse(notYet)));
        Assert.True(QuickTunnelStrategy.IsPublishedAnswer(JsonDocument.Parse(published)));
        Assert.False(
            QuickTunnelStrategy.IsPublishedAnswer(JsonDocument.Parse(existsWithoutAddress))
        );
        Assert.False(QuickTunnelStrategy.IsPublishedAnswer(JsonDocument.Parse(onlyACname)));
    }

    [Fact]
    public async Task TeardownAsync_ClearsThePublishedUrl()
    {
        ConnectivityStatus status = new() { PublicUrl = "https://old-name.trycloudflare.com" };
        QuickTunnelStrategy strategy = new(
            NullLogger<QuickTunnelStrategy>.Instance,
            status,
            binaryExists: () => true
        );

        await strategy.TeardownAsync();

        // A name from a run that is over must not stay advertised by anything.
        Assert.Null(status.PublicUrl);
    }
}
