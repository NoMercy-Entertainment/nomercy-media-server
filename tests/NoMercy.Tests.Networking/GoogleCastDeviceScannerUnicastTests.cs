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
using Makaretu.Dns;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Discovery;
using Xunit;
using Message = Makaretu.Dns.Message;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: a TV whose multicast replies never reach this host must still be
/// found, by asking with the QU bit and by querying known LAN addresses directly.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GoogleCastDeviceScannerUnicastTests
{
    private const string KnownIp = "192.0.2.10";

    private sealed class FixedTargets(params string[] ips) : ICastProbeTargets
    {
        public Task<IReadOnlyCollection<string>> GetLanIpsAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyCollection<string>>(ips);
    }

    private static Message Reply(string id, string name)
    {
        Message msg = new();
        msg.AdditionalRecords.Add(
            new TXTRecord { Strings = [$"id={id}", $"fn={name}", "md=Test Cast"] }
        );
        return msg;
    }

    private static GoogleCastDeviceScanner Build(
        NonBindingMulticastTransport transport,
        params string[] targets
    ) => new(NullLogger<GoogleCastDeviceScanner>.Instance, transport, new FixedTargets(targets));

    [Fact]
    public void Start_SendsQuQuery()
    {
        NonBindingMulticastTransport transport = new();
        using GoogleCastDeviceScanner scanner = Build(transport);
        using CancellationTokenSource cts = new();

        scanner.Start(cts.Token);

        Assert.Single(transport.UnicastResponseQueries);
        Assert.Equal("_googlecast._tcp.local", transport.UnicastResponseQueries[0].ToString());
    }

    [Fact]
    public void Probe_SendsQuQuery()
    {
        NonBindingMulticastTransport transport = new();
        using GoogleCastDeviceScanner scanner = Build(transport);

        // The unstarted fake service rejects the multicast query; the QU query goes out first.
        Record.Exception(() => scanner.Probe());

        Assert.Single(transport.UnicastResponseQueries);
    }

    [Fact]
    public async Task ProbeHosts_UnicastReplyFromKnownIp_BecomesSeenHit()
    {
        NonBindingMulticastTransport transport = new();
        transport.HostReplies[IPAddress.Parse(KnownIp)] = Reply("cast-id-1", "Test Room TV");
        using GoogleCastDeviceScanner scanner = Build(transport, KnownIp);

        await scanner.ProbeHostsAsync(CancellationToken.None);

        CastMdnsHit hit = Assert.Single(scanner.GetSeen());
        Assert.Equal("cast-id-1", hit.Id);
        Assert.Equal("Test Room TV", hit.FriendlyName);
        Assert.Equal(KnownIp, hit.Ip);
    }

    [Fact]
    public async Task ProbeHosts_AddressSeenBefore_IsQueriedAgainWithoutATarget()
    {
        NonBindingMulticastTransport transport = new();
        transport.HostReplies[IPAddress.Parse(KnownIp)] = Reply("cast-id-1", "Test Room TV");
        using GoogleCastDeviceScanner first = Build(transport, KnownIp);
        await first.ProbeHostsAsync(CancellationToken.None);
        transport.HostQueries.Clear();

        await first.ProbeHostsAsync(CancellationToken.None);

        Assert.Equal([IPAddress.Parse(KnownIp)], transport.HostQueries);
    }

    [Fact]
    public async Task ProbeHosts_NoReply_LeavesNothingSeen()
    {
        NonBindingMulticastTransport transport = new();
        using GoogleCastDeviceScanner scanner = Build(transport, KnownIp);

        await scanner.ProbeHostsAsync(CancellationToken.None);

        Assert.Empty(scanner.GetSeen());
    }
}
