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
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.Controllers.V1.Plugins;
using NoMercy.Api.DTOs.Common;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Media;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// The route a player fetches a plugin's media through. A live HLS player
/// asks for the same playlist every few seconds for as long as the viewer
/// watches, so the ticket must keep playing while it is asked for, and stop
/// once nobody asks.
/// </summary>
[Trait("Category", "Unit")]
public class PluginMediaControllerTests
{
    private static readonly Ulid LiveTv = Ulid.Parse("01J9ZK5V8Y0000000000000000");
    private static readonly Guid Viewer = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_live_playlist_polled_every_few_seconds_plays_past_fifteen_minutes()
    {
        MovableClock clock = new(Start);
        PluginMediaTicketMinter minter = Minter(clock);
        string ticket = MintPlaylist(minter);
        PluginMediaController controller = Controller(minter);

        for (
            TimeSpan at = TimeSpan.Zero;
            at <= TimeSpan.FromMinutes(45);
            at += TimeSpan.FromSeconds(6)
        )
        {
            clock.Now = Start + at;

            IActionResult result = await controller.Media(LiveTv, ticket, default);

            result.Should().BeOfType<FileStreamResult>($"the player is still polling at {at}");
        }
    }

    [Fact]
    public async Task A_live_playlist_nobody_polls_any_more_stops_playing()
    {
        MovableClock clock = new(Start);
        PluginMediaTicketMinter minter = Minter(clock);
        string ticket = MintPlaylist(minter);
        PluginMediaController controller = Controller(minter);

        clock.Now = Start.AddMinutes(14);
        (await controller.Media(LiveTv, ticket, default)).Should().BeOfType<FileStreamResult>();

        clock.Now = Start.AddMinutes(14) + PluginMediaTicketMinter.IdleTimeout;
        IActionResult result = await controller.Media(LiveTv, ticket, default);

        ObjectResult refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        refused
            .Value.Should()
            .BeOfType<DataResponseDto<PluginRefusal>>()
            .Which.Data!.Code.Should()
            .Be(PluginRefusalCodes.MediaTicketExpired);
    }

    /// <summary>A request that is refused does not count as playing.</summary>
    [Fact]
    public async Task A_request_from_another_account_does_not_keep_the_ticket_alive()
    {
        MovableClock clock = new(Start);
        PluginMediaTicketMinter minter = Minter(clock);
        string ticket = MintPlaylist(minter);

        clock.Now = Start.AddMinutes(14);
        (await Controller(minter, Guid.NewGuid()).Media(LiveTv, ticket, default))
            .Should()
            .BeOfType<ObjectResult>();

        clock.Now = Start.AddMinutes(15);
        (await Controller(minter).Media(LiveTv, ticket, default))
            .Should()
            .BeOfType<ObjectResult>("nobody it was minted for ever played it");
    }

    private static PluginMediaTicketMinter Minter(TimeProvider clock) =>
        new(clock, "a-server-key-that-is-long-enough-for-hmac"u8.ToArray());

    private static string MintPlaylist(PluginMediaTicketMinter minter) =>
        minter.Mint(
            LiveTv,
            Viewer,
            "https://iptv.example.com/channel/1/index.m3u8",
            PluginMediaProxy.TicketLifetime
        );

    private static PluginMediaController Controller(
        PluginMediaTicketMinter minter,
        Guid? asking = null
    ) =>
        new(minter, new EveryoneHasAccess(), new PlaylistMedia())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, (asking ?? Viewer).ToString())],
                            "TestAuth"
                        )
                    ),
                },
            },
        };

    private sealed class EveryoneHasAccess : IPluginAccessResolver
    {
        public PluginAccess Resolve(Ulid pluginId, Guid userId) => PluginAccess.Owned;
    }

    private sealed class PlaylistMedia : IPluginMediaFactory, IPluginMediaFetcher
    {
        public IPluginMedia CreateFor(Ulid pluginId) => throw new NotSupportedException();

        public IPluginMediaFetcher FetcherFor(Ulid pluginId) => this;

        public Task<HttpResponseMessage> FetchAsync(
            PluginProxyRequest request,
            string? range,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg.ts\n",
                        Encoding.UTF8,
                        "application/vnd.apple.mpegurl"
                    ),
                }
            );
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
