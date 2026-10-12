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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Connectivity;
using NoMercy.NmSystem.Networking;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed class ReachabilityCheckerTests
{
    private static CancellationTokenSource TestTimeout => new(TimeSpan.FromSeconds(5));

    // Loopback, so a test run never asks the firewall (see TestListenersBindLoopbackGuardTests).
    private static readonly IPAddress[] Loopbacks = [IPAddress.Loopback, IPAddress.IPv6Loopback];

    private sealed class RecordingProbe(
        ReachCodes codes,
        ReachabilityVerdict ipv4,
        ReachabilityVerdict ipv6,
        int? portThatMustBeBound = null
    ) : IReachabilityProbe
    {
        public ConcurrentBag<(AddressFamily Family, int Port, string Code)> Calls { get; } = [];
        public bool CodeWasLiveDuringTheCall { get; private set; } = true;
        public bool PortWasBoundDuringTheCall { get; private set; } = true;

        public Task<ReachabilityVerdict> ProbeBackAsync(
            AddressFamily family,
            int port,
            string code,
            CancellationToken ct
        )
        {
            Calls.Add((family, port, code));
            if (codes.Match(ReachCodes.PathPrefix + code) is null)
                CodeWasLiveDuringTheCall = false;
            if (portThatMustBeBound is int bound && !IsBound(bound))
                PortWasBoundDuringTheCall = false;

            return Task.FromResult(family == AddressFamily.InterNetworkV6 ? ipv6 : ipv4);
        }
    }

    private static bool IsBound(int port)
    {
        TcpListener attempt = new(IPAddress.Loopback, port);
        try
        {
            attempt.Start();
            attempt.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task ItAsksTheApiToCallBackOnBothAddressFamilies_OnTheExternalPort()
    {
        ReachCodes codes = new();
        RecordingProbe probe = new(
            codes,
            ReachabilityVerdict.Reachable,
            ReachabilityVerdict.Unknown
        );
        ReachabilityChecker checker = new(
            probe,
            NullLogger<ReachabilityChecker>.Instance,
            codes,
            internalPort: () => FreePort(),
            externalPort: () => 8443
        );

        ReachabilityReport report = await checker.CheckAsync(TestTimeout.Token);

        Assert.Equal(ReachabilityVerdict.Reachable, report.Ipv4);
        Assert.Equal(ReachabilityVerdict.Unknown, report.Ipv6);
        Assert.Equal(
            [AddressFamily.InterNetwork, AddressFamily.InterNetworkV6],
            probe.Calls.Select(c => c.Family).Order().ToArray()
        );
        Assert.All(probe.Calls, c => Assert.Equal(8443, c.Port));
        Assert.Single(probe.Calls.Select(c => c.Code).Distinct());
    }

    [Fact]
    public async Task TheCodeIsLiveDuringTheCall_AndDeadAfterIt()
    {
        ReachCodes codes = new();
        RecordingProbe probe = new(
            codes,
            ReachabilityVerdict.Reachable,
            ReachabilityVerdict.Reachable
        );
        ReachabilityChecker checker = new(
            probe,
            NullLogger<ReachabilityChecker>.Instance,
            codes,
            internalPort: () => FreePort(),
            externalPort: () => 7626,
            listenAddresses: Loopbacks
        );

        await checker.CheckAsync(TestTimeout.Token);

        Assert.True(probe.CodeWasLiveDuringTheCall);
        string code = probe.Calls.First().Code;
        Assert.Null(codes.Match(ReachCodes.PathPrefix + code));
    }

    [Fact]
    public async Task BeforeTheServerIsUp_TheListenerHoldsTheInternalPortDuringTheCall_ThenFreesIt()
    {
        ReachCodes codes = new();
        int internalPort = FreePort();
        RecordingProbe probe = new(
            codes,
            ReachabilityVerdict.Reachable,
            ReachabilityVerdict.Unknown,
            portThatMustBeBound: internalPort
        );
        ReachabilityChecker checker = new(
            probe,
            NullLogger<ReachabilityChecker>.Instance,
            codes,
            internalPort: () => internalPort,
            externalPort: () => 7626,
            listenAddresses: Loopbacks
        );

        await checker.CheckBeforeHostStartsAsync(TestTimeout.Token);

        Assert.True(probe.PortWasBoundDuringTheCall);
        Assert.False(IsBound(internalPort), "Kestrel must find the port free after the check");
    }

    [Fact]
    public async Task TheBootReport_IsHandedOutOnce()
    {
        ReachCodes codes = new();
        ReachabilityChecker checker = new(
            new RecordingProbe(codes, ReachabilityVerdict.Reachable, ReachabilityVerdict.Unknown),
            NullLogger<ReachabilityChecker>.Instance,
            codes,
            internalPort: () => FreePort(),
            externalPort: () => 7626,
            listenAddresses: Loopbacks
        );

        await checker.CheckBeforeHostStartsAsync(TestTimeout.Token);

        // The first evaluation after start uses the boot answer; every later one is a new
        // question, because the network may have changed since.
        Assert.NotNull(checker.TakeBootReport());
        Assert.Null(checker.TakeBootReport());
    }

    [Fact]
    public async Task AStaleBootReport_IsNotHandedOut()
    {
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        ReachCodes codes = new();
        ReachabilityChecker checker = new(
            new RecordingProbe(codes, ReachabilityVerdict.Reachable, ReachabilityVerdict.Unknown),
            NullLogger<ReachabilityChecker>.Instance,
            codes,
            internalPort: () => FreePort(),
            externalPort: () => 7626,
            now: () => now,
            listenAddresses: Loopbacks
        );

        await checker.CheckBeforeHostStartsAsync(TestTimeout.Token);
        now = now.AddMinutes(5);

        Assert.Null(checker.TakeBootReport());
    }
}
