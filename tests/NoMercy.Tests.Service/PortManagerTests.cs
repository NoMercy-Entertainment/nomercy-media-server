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
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Certificate;
using NoMercy.Service.Hosting;

namespace NoMercy.Tests.Service;

/// <summary>
/// Port selection and blocking-process identification are the boot step that
/// used to cascade into "Failed to start database job workers". These lock the
/// deterministic pieces: the availability probe, the next-free scan, and the
/// netstat/lsof PID parsers (tested directly so both OS formats are covered on
/// every host, not just the one the tests happen to run on).
/// </summary>
[Trait("Category", "Unit")]
public class PortManagerTests
{
    // Loopback, never the wildcard: a wildcard probe asks the Windows firewall
    // on every test run.
    private static PortManager BuildManager() =>
        new(NullLogger<PortManager>.Instance, new StubCertificateService(), IPAddress.Loopback);

    private static int GetFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void IsPortAvailable_ProbesTheAddressItWasGiven()
    {
        // A probe on the wildcard address makes Windows ask the firewall on
        // every test run. Tests probe loopback; production keeps the wildcard.
        PortManager loopback = new(
            NullLogger<PortManager>.Instance,
            new StubCertificateService(),
            IPAddress.Loopback
        );
        PortManager production = new(
            NullLogger<PortManager>.Instance,
            new StubCertificateService()
        );

        Assert.Equal(IPAddress.Loopback, loopback.ProbeAddress);
        Assert.Equal(IPAddress.Any, production.ProbeAddress);
    }

    [Fact]
    public void IsPortAvailable_FreePort_ReturnsTrue()
    {
        PortManager manager = BuildManager();

        bool available = manager.IsPortAvailable(GetFreePort());

        Assert.True(available);
    }

    [Fact]
    public void IsPortAvailable_OccupiedPort_ReturnsFalse()
    {
        int port = GetFreePort();
        TcpListener holder = new(IPAddress.Loopback, port);
        holder.Start();
        try
        {
            PortManager manager = BuildManager();

            bool available = manager.IsPortAvailable(port);

            Assert.False(available);
        }
        finally
        {
            holder.Stop();
        }
    }

    [Fact]
    public void FindNextAvailablePort_StartFree_ReturnsStartPort()
    {
        int port = GetFreePort();
        PortManager manager = BuildManager();

        int found = manager.FindNextAvailablePort(port);

        Assert.Equal(port, found);
    }

    [Fact]
    public void FindNextAvailablePort_StartOccupied_ReturnsHigherFreePort()
    {
        int port = GetFreePort();
        TcpListener holder = new(IPAddress.Loopback, port);
        holder.Start();
        try
        {
            PortManager manager = BuildManager();

            int found = manager.FindNextAvailablePort(port);

            Assert.True(found > port, $"expected a port past the occupied {port}, got {found}");
            Assert.True(manager.IsPortAvailable(found));
        }
        finally
        {
            holder.Stop();
        }
    }

    [Theory]
    [InlineData(["  TCP    0.0.0.0:7626    0.0.0.0:0    LISTENING    1234", 1234])]
    [InlineData(["TCP    [::]:7626    [::]:0    LISTENING    98765\r\n", 98765])]
    public void ParsePidFromNetstat_ValidListeningRow_ReturnsPid(string netstat, int expected)
    {
        Assert.Equal(expected, PortManager.ParsePidFromNetstat(netstat));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TCP    0.0.0.0:7626    0.0.0.0:0    LISTENING    not-a-pid")]
    public void ParsePidFromNetstat_EmptyOrMalformed_ReturnsMinusOne(string netstat)
    {
        Assert.Equal(-1, PortManager.ParsePidFromNetstat(netstat));
    }

    [Fact]
    public void ParsePidFromLsof_ValidDataRow_ReturnsPid()
    {
        const string lsof =
            "COMMAND   PID   USER   FD   TYPE   DEVICE   SIZE/OFF   NODE   NAME\n"
            + "NoMercyMe 4321  nomercy  10u  IPv4  0x0        0t0        TCP    *:7626 (LISTEN)\n";

        Assert.Equal(4321, PortManager.ParsePidFromLsof(lsof));
    }

    [Fact]
    public void ParsePidFromLsof_EstablishedClientBeforeListener_ReturnsListenerPid()
    {
        const string lsof =
            "COMMAND   PID   USER   FD   TYPE   DEVICE   SIZE/OFF   NODE   NAME\n"
            + "browser   100   user   12u  IPv4   0x1      0t0        TCP    localhost:49152->localhost:7626 (ESTABLISHED)\n"
            + "NoMercyMe 200   user   10u  IPv4   0x2      0t0        TCP    *:7626 (LISTEN)\n";

        Assert.Equal(200, PortManager.ParsePidFromLsof(lsof));
    }

    [Theory]
    [InlineData("")]
    [InlineData("COMMAND   PID   USER   FD   TYPE   DEVICE   SIZE/OFF   NODE   NAME\n")]
    public void ParsePidFromLsof_EmptyOrHeaderOnly_ReturnsMinusOne(string lsof)
    {
        Assert.Equal(-1, PortManager.ParsePidFromLsof(lsof));
    }

    [Fact]
    public async Task EnsurePortAvailable_PortAlreadyFree_ReturnsImmediatelyWithoutConsultingCertService()
    {
        int port = GetFreePort();
        Mock<ICertificateService> certificateService = new(MockBehavior.Strict);
        PortManager manager = new(NullLogger<PortManager>.Instance, certificateService.Object);

        await manager.EnsurePortAvailable(port);

        // Strict mock: any unexpected call (e.g. HasValidCertificate) would have
        // thrown above. Reaching here proves the early-return path never
        // touches the certificate service at all.
        certificateService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandlePortInUse_InnerExceptionIsNotSocketException_ReturnsFalseWithoutRecovering()
    {
        PortManager manager = BuildManager();
        IOException ex = new("disk full", new InvalidOperationException("not a socket error"));

        bool shouldRetry = await manager.HandlePortInUse(7626, ex);

        Assert.False(shouldRetry);
    }

    [Fact]
    public async Task HandlePortInUse_SocketErrorIsNotAddressInUse_ReturnsFalseWithoutRecovering()
    {
        PortManager manager = BuildManager();
        SocketException socketEx = new((int)SocketError.ConnectionRefused);
        IOException ex = new("connection refused", socketEx);

        bool shouldRetry = await manager.HandlePortInUse(7626, ex);

        Assert.False(shouldRetry);
    }

    [Fact]
    public async Task HandlePortInUse_AddressAlreadyInUseOnAFreePort_RecoversAndReturnsTrue()
    {
        // The exception CLAIMS the port collided, but the port is actually free
        // (a transient bind race) — EnsurePortAvailable's happy path resolves it
        // without touching the certificate service, and HandlePortInUse reports
        // the caller may safely retry.
        int port = GetFreePort();
        PortManager manager = BuildManager();
        SocketException socketEx = new((int)SocketError.AddressAlreadyInUse);
        IOException ex = new("address already in use", socketEx);

        bool shouldRetry = await manager.HandlePortInUse(port, ex);

        Assert.True(shouldRetry);
    }

    private sealed class StubCertificateService : ICertificateService
    {
        public void LoadFromDb() { }

        public bool HasValidCertificate() => false;

        public bool EnsureHttpsCertificate() => false;

        public void KestrelConfig(KestrelServerOptions options) { }

        public void ConfigureHttpsListener(ListenOptions listenOptions) { }

        public Task RenewSslCertificate(string? accessToken, int maxRetries = 30) =>
            Task.CompletedTask;
    }
}
