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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Discovery;
using Sharpcaster.Models;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: every public control method on ChromeCastService (Launch,
/// LaunchAndroidReceiver, CastPlaylist, Stop, Disconnect,
/// GetChromecastStatus, GetMediaStatus, SelectChromecast) must no-op safely —
/// never throw — when there is no target name to act on and/or no live
/// client in the pool for that name. This is the decision logic a caller
/// depends on before a Chromecast has ever been discovered or connected.
/// Connecting to (or receiving RECEIVER_STATUS from) a real Chromecast
/// requires an actual device on the LAN and is itemized as not
/// unit-testable — see the coverage report.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChromeCastServiceGuardClauseTests
{
    private sealed class NoOpNetworkDiscovery : INetworkDiscovery
    {
        public string InternalIp { get; set; } = "192.168.1.1";
        public string RegistrationInternalIp => InternalIp;
        public string ExternalIp { get; set; } = "1.2.3.4";
        public string? InternalIpV6 => null;
        public string? ExternalIpV6 { get; set; }
        public string InternalDomain => string.Empty;
        public string InternalAddress => string.Empty;
        public string ExternalDomain => string.Empty;
        public string ExternalAddress => "https://external.example.com:7626";
        public string DirectExternalAddress => ExternalAddress;
        public string? ExternalAddressV6 => null;
        public bool Ipv6Enabled => false;

        public Task DiscoverExternalIpAsync() => Task.CompletedTask;

        public Task ForceRediscoveryAsync() => Task.CompletedTask;

        public Task<bool> IsPortOpenAsync() => Task.FromResult(false);

        public Task RemovePortMappingsAsync() => Task.CompletedTask;
    }

    private static ChromeCastService BuildService() =>
        new(NullLogger<ChromeCastService>.Instance, new NoOpNetworkDiscovery());

    [Fact]
    public void GetChromeCasts_BeforeInit_ReturnsEmptyArray()
    {
        ChromeCastService service = BuildService();

        string[] receivers = service.GetChromeCasts();

        Assert.Empty(receivers);
    }

    [Fact]
    public async Task FindReceiverNameByIpAsync_NullIp_ReturnsNull()
    {
        ChromeCastService service = BuildService();

        string? name = await service.FindReceiverNameByIpAsync(null!);

        Assert.Null(name);
    }

    [Fact]
    public async Task FindReceiverNameByIpAsync_EmptyIp_ReturnsNull()
    {
        ChromeCastService service = BuildService();

        string? name = await service.FindReceiverNameByIpAsync(string.Empty);

        Assert.Null(name);
    }

    // A device row stores the address the client connected FROM. Anyone watching from
    // outside the LAN registers a public address, and treating that as a Cast candidate
    // made the server rescan mDNS for it, synthesize a receiver named after the WAN IP,
    // and open a TCP connection to the owner's own router on every device listing.
    [Theory]
    [InlineData("12.345.678.90")]
    [InlineData("8.8.8.8")]
    [InlineData("3a02:ab46f:6c1g::2")]
    [InlineData("not-an-ip")]
    public async Task FindReceiverNameByIpAsync_AddressOffThisNetwork_ReturnsNull(string ip)
    {
        ChromeCastService service = BuildService();

        string? name = await service.FindReceiverNameByIpAsync(ip);

        Assert.Null(name);
    }

    [Fact]
    public async Task FindReceiverNameByIpAsync_LanAddress_StillSynthesizesAReceiver()
    {
        ChromeCastService service = BuildService();

        string? name = await service.FindReceiverNameByIpAsync("192.168.2.31");

        Assert.Equal("192.168.2.31", name);
    }

    [Fact]
    public async Task SelectChromecast_NullReceiver_DoesNotThrow()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() =>
            service.SelectChromecast((ChromecastReceiver?)null)
        );

        Assert.Null(ex);
    }

    [Fact]
    public async Task SelectChromecast_UnknownName_DoesNotThrow()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() =>
            service.SelectChromecast("no-such-receiver")
        );

        Assert.Null(ex);
    }

    [Fact]
    public async Task Launch_NoNameAndNoPriorSelection_ReturnsWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() => service.Launch());

        Assert.Null(ex);
    }

    [Fact]
    public async Task Launch_NameNotInPool_ReturnsWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() => service.Launch("unknown-receiver"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task LaunchAndroidReceiver_NoNameAndNoPriorSelection_ReturnsWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() => service.LaunchAndroidReceiver());

        Assert.Null(ex);
    }

    [Fact]
    public async Task LaunchAndroidReceiver_NameNotInPool_ReturnsWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() =>
            service.LaunchAndroidReceiver("unknown-receiver")
        );

        Assert.Null(ex);
    }

    [Fact]
    public async Task CastPlaylist_NoNameAndNoPriorSelection_ReturnsWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() => service.CastPlaylist("movie/129"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task CastPlaylist_NameNotInPool_ReturnsWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() =>
            service.CastPlaylist("movie/129", "unknown-receiver")
        );

        Assert.Null(ex);
    }

    [Fact]
    public void GetChromecastStatus_NoNameAndNoPriorSelection_ReturnsNull()
    {
        ChromeCastService service = BuildService();

        Sharpcaster.Models.ChromecastStatus.ChromecastStatus? status =
            service.GetChromecastStatus();

        Assert.Null(status);
    }

    [Fact]
    public void GetChromecastStatus_NameNotInPool_ReturnsNull()
    {
        ChromeCastService service = BuildService();

        Sharpcaster.Models.ChromecastStatus.ChromecastStatus? status = service.GetChromecastStatus(
            "unknown-receiver"
        );

        Assert.Null(status);
    }

    [Fact]
    public void GetMediaStatus_NoNameAndNoPriorSelection_ReturnsNull()
    {
        ChromeCastService service = BuildService();

        Sharpcaster.Models.Media.MediaStatus? status = service.GetMediaStatus();

        Assert.Null(status);
    }

    [Fact]
    public void GetMediaStatus_NameNotInPool_ReturnsNull()
    {
        ChromeCastService service = BuildService();

        Sharpcaster.Models.Media.MediaStatus? status = service.GetMediaStatus("unknown-receiver");

        Assert.Null(status);
    }

    [Fact]
    public async Task Stop_NoNameAndNoPriorSelection_ReturnsWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() => service.Stop());

        Assert.Null(ex);
    }

    [Fact]
    public async Task Stop_NameNotInPool_ReturnsWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() => service.Stop("unknown-receiver"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Disconnect_NoNameAndNoPriorSelection_ReturnsWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() => service.Disconnect());

        Assert.Null(ex);
    }

    [Fact]
    public async Task Disconnect_NameNotInPool_ReturnsWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() => service.Disconnect("unknown-receiver"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Disconnect_Wildcard_WithEmptyPool_CompletesWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(() => service.Disconnect("*"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task DisconnectAllAsync_EmptyPool_CompletesWithoutThrowing()
    {
        ChromeCastService service = BuildService();

        Exception? ex = await Record.ExceptionAsync(service.DisconnectAllAsync);

        Assert.Null(ex);
    }
}
