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
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Lan;
using NoMercy.PluginSdk.Media;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A link minted while a LAN route serves a device is the device's, not an
/// account's: it reads back for that device and plugin only, never plays for
/// a user, and is forgotten when the device is revoked.
/// </summary>
public class PluginMediaDeviceTicketTests
{
    private static readonly Ulid LiveTv = Ulid.Parse("01J9ZK5V8Y00000000000000C1");
    private static readonly Ulid Radio = Ulid.Parse("01J9ZK5V8Y00000000000000C2");
    private static readonly Guid Viewer = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Noon = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly PluginLanDispatch Tuner = new(LiveTv, "tuner-1", "Living room TV");

    [Fact]
    public async Task A_link_minted_while_a_lan_route_serves_a_device_is_that_devices()
    {
        PluginMediaTicketMinter minter = Minter();

        PluginMediaUrl url = await Proxy(minter, Tuner)
            .MintAsync(Request("https://iptv.example.com/1.ts"));

        url.Url.ToString().Should().StartWith($"/api/v1/plugins/{LiveTv}/lan-media/");
        minter.ReadForDevice(TicketOf(url), LiveTv)!.DeviceId.Should().Be("tuner-1");
        minter.Read(TicketOf(url))!.UserId.Should().Be(Guid.Empty);
    }

    /// <summary>
    /// The LAN route takes the user off the request, but if anything were left
    /// the link still goes to the device: what is asking is the device.
    /// </summary>
    [Fact]
    public async Task The_device_wins_over_anything_left_of_a_user()
    {
        PluginMediaTicketMinter minter = Minter();

        PluginMediaUrl url = await Proxy(minter, Tuner, Viewer)
            .MintAsync(Request("https://iptv.example.com/1.ts"));

        minter.Read(TicketOf(url))!.DeviceId.Should().Be("tuner-1");
        minter.Refuse(TicketOf(url), Viewer).Should().NotBeNull();
    }

    [Fact]
    public async Task Without_a_device_a_link_is_still_the_users()
    {
        PluginMediaTicketMinter minter = Minter();

        PluginMediaUrl url = await Proxy(minter, null, Viewer)
            .MintAsync(Request("https://iptv.example.com/1.ts"));

        url.Url.ToString().Should().StartWith($"/api/v1/plugins/{LiveTv}/media/");
        minter.Refuse(TicketOf(url), Viewer).Should().BeNull();
    }

    [Fact]
    public async Task A_plugin_cannot_mint_for_a_device_that_reached_another_plugin()
    {
        Func<Task> mint = () =>
            Proxy(Minter(), Tuner with { PluginId = Radio })
                .MintAsync(Request("https://iptv.example.com/1.ts"));

        await mint.Should().ThrowAsync<PluginRefusedException>();
    }

    [Fact]
    public async Task A_playlist_a_device_plays_hands_it_children_bound_to_it()
    {
        PluginMediaTicketMinter minter = Minter();
        PluginMediaProxy proxy = Proxy(
            minter,
            Tuner,
            answer: _ =>
                new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg1.ts\n#EXTINF:6,\nseg2.ts\n",
                        Encoding.UTF8,
                        "application/vnd.apple.mpegurl"
                    ),
                }
        );

        HttpResponseMessage response = await proxy.FetchAsync(
            Request("https://iptv.example.com/live.m3u8"),
            null,
            default
        );
        string[] children =
        [
            .. (await response.Content.ReadAsStringAsync())
                .Split('\n')
                .Where(line => line.StartsWith('/')),
        ];

        children.Should().HaveCount(2);
        children
            .Should()
            .AllSatisfy(child =>
            {
                child.Should().StartWith($"/api/v1/plugins/{LiveTv}/lan-media/");
                minter.ReadForDevice(child.Split('/')[^1], LiveTv)!.DeviceId.Should().Be("tuner-1");
            });
    }

    [Fact]
    public void A_device_ticket_reads_only_for_its_own_plugin()
    {
        PluginMediaTicketMinter minter = Minter();
        string ticket = minter.MintForDevice(
            LiveTv,
            "tuner-1",
            Request("https://a/1.ts"),
            Lifetime
        );

        minter.ReadForDevice(ticket, LiveTv).Should().NotBeNull();
        minter.ReadForDevice(ticket, Radio).Should().BeNull();
    }

    [Fact]
    public void A_users_ticket_never_reads_as_a_devices()
    {
        PluginMediaTicketMinter minter = Minter();
        string ticket = minter.Mint(LiveTv, Viewer, Request("https://a/1.ts"), Lifetime);

        minter.ReadForDevice(ticket, LiveTv).Should().BeNull();
    }

    [Fact]
    public void A_device_ticket_is_refused_for_every_account_and_for_nobody()
    {
        PluginMediaTicketMinter minter = Minter();
        string ticket = minter.MintForDevice(
            LiveTv,
            "tuner-1",
            Request("https://a/1.ts"),
            Lifetime
        );

        minter.Refuse(ticket, Viewer)!.Code.Should().Be(PluginRefusalCodes.MediaTicketUserMismatch);
        minter.Refuse(ticket, Guid.Empty).Should().NotBeNull();
        minter.Refuse(ticket).Should().NotBeNull();
    }

    [Fact]
    public void Something_that_is_not_a_ticket_reads_as_no_device_ticket()
    {
        PluginMediaTicketMinter minter = Minter();

        minter.ReadForDevice("not-a-ticket", LiveTv).Should().BeNull();
        minter.ReadForDevice("still.not", LiveTv).Should().BeNull();
    }

    [Fact]
    public void A_device_file_ticket_keeps_its_path_and_its_device()
    {
        PluginMediaTicketMinter minter = Minter();
        string ticket = minter.MintFileForDevice(LiveTv, "tuner-1", "recordings/news.ts", Lifetime);

        PluginMediaTicket read = minter.ReadForDevice(ticket, LiveTv)!;

        read.FilePath.Should().Be("recordings/news.ts");
        read.DeviceId.Should().Be("tuner-1");
    }

    [Fact]
    public void Forgetting_a_device_forgets_its_tickets_and_no_one_elses()
    {
        PluginMediaTicketMinter minter = Minter();
        string tuner = minter.MintForDevice(LiveTv, "tuner-1", Request("https://a/1.ts"), Lifetime);
        string bedroom = minter.MintForDevice(
            LiveTv,
            "tuner-2",
            Request("https://a/1.ts"),
            Lifetime
        );
        string sameIdOtherPlugin = minter.MintForDevice(
            Radio,
            "tuner-1",
            Request("https://a/1.ts"),
            Lifetime
        );
        string user = minter.Mint(LiveTv, Viewer, Request("https://a/1.ts"), Lifetime);

        minter.ForgetDevice(LiveTv, "tuner-1");

        minter.ReadForDevice(tuner, LiveTv).Should().BeNull();
        minter.ReadForDevice(bedroom, LiveTv).Should().NotBeNull();
        minter.ReadForDevice(sameIdOtherPlugin, Radio).Should().NotBeNull();
        minter.Refuse(user, Viewer).Should().BeNull();
    }

    [Fact]
    public void Revoking_a_device_forgets_every_ticket_it_was_handed()
    {
        PluginMediaTicketMinter tickets = Minter();
        PluginLanDeviceMinter devices = new(new InMemoryStore(), new FixedClock(Noon), tickets);
        PluginLanDeviceMinted device = devices.Mint(LiveTv, "Living room TV");
        string ticket = tickets.MintForDevice(
            LiveTv,
            device.DeviceId,
            Request("https://a/1.ts"),
            Lifetime
        );

        devices.Revoke(LiveTv, device.DeviceId);

        tickets.ReadForDevice(ticket, LiveTv).Should().BeNull();
    }

    private static TimeSpan Lifetime => PluginMediaProxy.TicketLifetime;

    private static PluginMediaTicketMinter Minter() =>
        new(new FixedClock(Noon), "a-server-key-that-is-long-enough-for-hmac"u8.ToArray());

    private static PluginProxyRequest Request(string url) =>
        new() { Links = [new() { Url = new(url) }] };

    private static string TicketOf(PluginMediaUrl url) => url.Url.ToString().Split('/')[^1];

    private static PluginMediaProxy Proxy(
        PluginMediaTicketMinter minter,
        PluginLanDispatch? device,
        Guid? user = null,
        Func<HttpRequestMessage, HttpResponseMessage>? answer = null
    ) =>
        new(
            LiveTv,
            new StubCaller(user ?? Guid.Empty),
            new AllowAll(),
            minter,
            new HttpClient(
                new StubHandler(
                    answer ?? (_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) })
                )
            ),
            NullLogger.Instance,
            lanCaller: new StubLanCaller(device)
        );

    private sealed class StubCaller(Guid userId) : IPluginCallerAccessor
    {
        public Guid CurrentUserId => userId;
    }

    private sealed class StubLanCaller(PluginLanDispatch? device) : IPluginLanCallerAccessor
    {
        public PluginLanDispatch? Current => device;
    }

    private sealed class AllowAll : IPluginCapabilityBroker
    {
        public PluginRefusal? Check(Ulid pluginId, string capability, string? scope = null) => null;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            HttpResponseMessage response = answer(request);
            response.RequestMessage ??= request;

            return Task.FromResult(response);
        }
    }

    private sealed class InMemoryStore : IPluginLanDeviceStore
    {
        private readonly List<PluginLanDevice> _devices = [];

        public void Add(PluginLanDevice device) => _devices.Add(device);

        public PluginLanDevice? Find(Ulid pluginId, string deviceId) =>
            _devices.FirstOrDefault(device =>
                device.PluginId == pluginId && device.DeviceId == deviceId
            );

        public IReadOnlyList<PluginLanDevice> For(Ulid pluginId) =>
            [.. _devices.Where(device => device.PluginId == pluginId)];

        public void Revoke(Ulid pluginId, string deviceId)
        {
            int index = _devices.FindIndex(device =>
                device.PluginId == pluginId && device.DeviceId == deviceId
            );

            if (index >= 0)
                _devices[index] = _devices[index] with { Revoked = true };
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
