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
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Plugins;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.Plugins;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Lan;
using NoMercy.PluginSdk.Media;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// The route a device on the owner's network plays a link through, minted
/// for it while a LAN route served it. Bound to the device and the plugin,
/// answered on the LAN only, under the same lifetime as a user's link, and
/// gone the moment the owner revokes the device.
/// </summary>
[Trait("Category", "Unit")]
public class PluginLanMediaControllerTests
{
    private const string Lan = "192.168.1.20";

    private static readonly Ulid LiveTv = Ulid.Parse("01J9ZK5V8Y00000000000000B1");
    private static readonly Ulid Radio = Ulid.Parse("01J9ZK5V8Y00000000000000B2");
    private static readonly Guid Viewer = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);

    private static readonly PluginRefusal Refusal = PluginLanRefusal.For(LiveTv);

    [Fact]
    public async Task A_device_plays_its_own_link_from_the_lan()
    {
        Rig rig = new();
        string ticket = rig.MintFor(rig.Device);

        IActionResult result = await rig.Controller(Lan).LanMedia(LiveTv, ticket, default);

        result.Should().BeOfType<FileStreamResult>();
    }

    /// <summary>
    /// While a device plays a playlist, the proxy is asked for its children,
    /// so the request reads as that device and as nobody signed in.
    /// </summary>
    [Fact]
    public async Task While_a_device_plays_the_request_reads_as_that_device()
    {
        Rig rig = new();
        string ticket = rig.MintFor(rig.Device);
        PluginMediaController controller = rig.Controller(Lan, signedIn: true);

        await controller.LanMedia(LiveTv, ticket, default);

        HttpContext http = controller.HttpContext;
        http.Features.Get<PluginLanDispatch>()
            .Should()
            .Be(new PluginLanDispatch(LiveTv, rig.Device.DeviceId, rig.Device.Name));
        http.User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Theory]
    [InlineData("203.0.113.9", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData(Lan, "X-Forwarded-For")]
    [InlineData(Lan, "CF-Connecting-IP")]
    public async Task A_device_link_never_plays_from_outside_the_lan(string peer, string? relay)
    {
        Rig rig = new();
        string ticket = rig.MintFor(rig.Device);

        IActionResult result = await rig.Controller(peer, relayHeader: relay)
            .LanMedia(LiveTv, ticket, default);

        RefusalOf(result).Should().Be(Refusal);
        rig.Media.Fetched.Should().Be(0);
    }

    [Fact]
    public async Task A_users_link_does_not_play_on_the_lan_route()
    {
        Rig rig = new();
        string ticket = rig.Tickets.Mint(
            LiveTv,
            Viewer,
            Request(),
            PluginMediaProxy.TicketLifetime
        );

        RefusalOf(await rig.Controller(Lan).LanMedia(LiveTv, ticket, default)).Should().Be(Refusal);
    }

    [Fact]
    public async Task A_device_link_does_not_reach_another_plugin()
    {
        Rig rig = new();
        string ticket = rig.MintFor(rig.Device);

        IActionResult result = await rig.Controller(Lan).LanMedia(Radio, ticket, default);

        RefusalOf(result).Should().Be(PluginLanRefusal.For(Radio));
        rig.Media.Fetched.Should().Be(0);
    }

    [Fact]
    public async Task A_link_from_another_device_of_another_plugin_does_not_play()
    {
        Rig rig = new();
        PluginLanDeviceMinted radioBox = rig.Devices.Mint(Radio, "Kitchen radio");
        string ticket = rig.Tickets.MintForDevice(
            Radio,
            radioBox.DeviceId,
            Request(),
            PluginMediaProxy.TicketLifetime
        );

        RefusalOf(await rig.Controller(Lan).LanMedia(LiveTv, ticket, default)).Should().Be(Refusal);
    }

    [Fact]
    public async Task Revoking_the_device_stops_every_link_it_was_handed_at_once()
    {
        Rig rig = new();
        string ticket = rig.MintFor(rig.Device);
        string other = rig.MintFor(rig.Device);

        rig.Devices.Revoke(LiveTv, rig.Device.DeviceId);

        RefusalOf(await rig.Controller(Lan).LanMedia(LiveTv, ticket, default)).Should().Be(Refusal);
        rig.Tickets.ReadForDevice(other, LiveTv).Should().BeNull("revoking forgets every link");
    }

    [Fact]
    public async Task Revoking_one_device_leaves_another_devices_links_playing()
    {
        Rig rig = new();
        PluginLanDeviceMinted bedroom = rig.Devices.Mint(LiveTv, "Bedroom TV");
        string ticket = rig.MintFor(bedroom);

        rig.Devices.Revoke(LiveTv, rig.Device.DeviceId);

        (await rig.Controller(Lan).LanMedia(LiveTv, ticket, default))
            .Should()
            .BeOfType<FileStreamResult>();
    }

    [Fact]
    public async Task Revoking_the_device_cuts_the_stream_it_is_reading()
    {
        Rig rig = new();
        string ticket = rig.MintFor(rig.Device);
        PluginMediaController controller = rig.Controller(Lan);
        RecordingLifetime lifetime = new();
        controller.HttpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        (await controller.LanMedia(LiveTv, ticket, default)).Should().BeOfType<FileStreamResult>();
        lifetime.Aborted.Should().BeFalse();

        rig.Devices.Revoke(LiveTv, rig.Device.DeviceId);

        lifetime.Aborted.Should().BeTrue();
    }

    [Fact]
    public async Task A_plugin_that_no_longer_names_lan_routes_plays_no_device_link()
    {
        Rig rig = new(lanRoutes: false);
        string ticket = rig.MintFor(rig.Device);

        RefusalOf(await rig.Controller(Lan).LanMedia(LiveTv, ticket, default)).Should().Be(Refusal);
    }

    [Fact]
    public async Task A_live_playlist_a_device_keeps_polling_plays_past_fifteen_minutes()
    {
        Rig rig = new();
        string ticket = rig.MintFor(rig.Device);

        for (
            TimeSpan at = TimeSpan.Zero;
            at <= TimeSpan.FromMinutes(45);
            at += TimeSpan.FromSeconds(6)
        )
        {
            rig.Clock.Now = Start + at;

            (await rig.Controller(Lan).LanMedia(LiveTv, ticket, default))
                .Should()
                .BeOfType<FileStreamResult>($"the device is still polling at {at}");
        }
    }

    [Fact]
    public async Task A_device_link_nobody_plays_any_more_stops()
    {
        Rig rig = new();
        string ticket = rig.MintFor(rig.Device);

        rig.Clock.Now = Start.AddMinutes(14);
        (await rig.Controller(Lan).LanMedia(LiveTv, ticket, default))
            .Should()
            .BeOfType<FileStreamResult>();

        rig.Clock.Now = Start.AddMinutes(14) + PluginMediaTicketMinter.IdleTimeout;

        RefusalOf(await rig.Controller(Lan).LanMedia(LiveTv, ticket, default)).Should().Be(Refusal);
    }

    [Fact]
    public async Task A_device_link_never_played_runs_out_at_its_deadline()
    {
        Rig rig = new();
        string ticket = rig.MintFor(rig.Device);

        rig.Clock.Now = Start + PluginMediaProxy.TicketLifetime;

        RefusalOf(await rig.Controller(Lan).LanMedia(LiveTv, ticket, default)).Should().Be(Refusal);
    }

    [Fact]
    public async Task No_device_link_outlives_the_hard_limit_however_much_it_is_played()
    {
        Rig rig = new();
        string ticket = rig.MintFor(rig.Device);

        for (
            TimeSpan at = TimeSpan.Zero;
            at < PluginMediaTicketMinter.MaxLifetime;
            at += TimeSpan.FromMinutes(1)
        )
        {
            rig.Clock.Now = Start + at;
            await rig.Controller(Lan).LanMedia(LiveTv, ticket, default);
        }

        rig.Clock.Now = Start + PluginMediaTicketMinter.MaxLifetime;

        RefusalOf(await rig.Controller(Lan).LanMedia(LiveTv, ticket, default)).Should().Be(Refusal);
    }

    private static PluginRefusal RefusalOf(IActionResult result)
    {
        ObjectResult refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        return refused.Value.Should().BeOfType<DataResponseDto<PluginRefusal>>().Subject.Data!;
    }

    private static PluginProxyRequest Request() =>
        new() { Links = [new() { Url = new("https://iptv.example.com/channel/1/index.m3u8") }] };

    /// <summary>One plugin, one device, a clock the test moves, and a media fetcher that counts.</summary>
    private sealed class Rig
    {
        public Rig(bool lanRoutes = true)
        {
            Clock = new(Start);
            Tickets = new(Clock, "a-server-key-that-is-long-enough-for-hmac"u8.ToArray());
            Devices = new(new InMemoryLanDeviceStore(), Clock, Tickets);
            Device = Devices.Mint(LiveTv, "Living room TV");

            Mock<IPluginManager> plugins = new();
            plugins
                .Setup(manager => manager.GetPluginInfo(It.IsAny<Ulid>()))
                .Returns(
                    (Ulid id) =>
                        new PluginInfo
                        {
                            Id = id,
                            Name = "Live TV",
                            Description = "Channels.",
                            Version = new(1, 0),
                            Status = PluginStatus.Active,
                            Capabilities = new()
                            {
                                Rest = true,
                                Lan = lanRoutes ? new() { Routes = ["hdhr/lineup.json"] } : null,
                            },
                        }
                );
            Plugins = plugins.Object;
        }

        public MovableClock Clock { get; }
        public PluginMediaTicketMinter Tickets { get; }
        public PluginLanDeviceMinter Devices { get; }
        public PluginLanDeviceMinted Device { get; }
        public IPluginManager Plugins { get; }
        public CountingMedia Media { get; } = new();

        public string MintFor(PluginLanDeviceMinted device) =>
            Tickets.MintForDevice(
                LiveTv,
                device.DeviceId,
                Request(),
                PluginMediaProxy.TicketLifetime
            );

        public PluginMediaController Controller(
            string peer,
            bool signedIn = false,
            string? relayHeader = null
        )
        {
            DefaultHttpContext http = new()
            {
                Connection = { RemoteIpAddress = IPAddress.Parse(peer) },
            };

            if (relayHeader is not null)
                http.Request.Headers[relayHeader] = "192.168.1.30";

            if (signedIn)
                http.User = new(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, Viewer.ToString())],
                        "TestAuth"
                    )
                );

            return new(Tickets, new EveryoneHasAccess(), Media, Devices, Plugins)
            {
                ControllerContext = new() { HttpContext = http },
            };
        }
    }

    private sealed class EveryoneHasAccess : IPluginAccessResolver
    {
        public PluginAccess Resolve(Ulid pluginId, Guid userId) => PluginAccess.Owned;
    }

    private sealed class CountingMedia : IPluginMediaFactory, IPluginMediaFetcher
    {
        public int Fetched { get; private set; }

        public IPluginMedia CreateFor(Ulid pluginId) => throw new NotSupportedException();

        public IPluginMediaFetcher FetcherFor(Ulid pluginId) => this;

        public Stream? OpenFile(string relativePath) => throw new NotSupportedException();

        public Task<HttpResponseMessage> FetchAsync(
            PluginProxyRequest request,
            string? range,
            CancellationToken ct
        )
        {
            Fetched++;

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x47]),
                }
            );
        }
    }

    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
