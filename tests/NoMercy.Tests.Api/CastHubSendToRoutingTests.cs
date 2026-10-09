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

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Data.Activity;
using NoMercy.Database;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Auth;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

// The 25 CastHub methods below (Play/Pause/Time/Ended/Volume/Muted/Item/
// Playlist/SubtitleTracks/CurrentSubtitleTrack/AudioTracks/CurrentAudioTrack/
// GetPlayerState/PlayerState/Set*) share one shape: resolve the caller's
// cached user, no-op when unresolved, otherwise forward a specific event name
// through IClientMessenger.SendTo to that user's other connections. The event
// NAME is the actual wire contract clients switch on — a silent rename (e.g.
// PlayerState's "MusicPlayerState" send) breaks every already-deployed client
// without a compile error anywhere. These tests build a real CastHub against
// the app's actual DI-configured MediaContext/UserCache (via NoMercyApiFactory)
// with IClientMessenger mocked — no real cast/broadcast ever happens.
[Trait("Category", "Characterization")]
public class CastHubSendToRoutingTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public CastHubSendToRoutingTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        // Force the test host to start so UserCache.Current is populated with
        // the seeded DefaultUserId.
        _factory.CreateClient();
    }

    private CastHub CreateHub(Guid callerUserId, out Mock<IClientMessenger> clientMessenger)
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();

        clientMessenger = new Mock<IClientMessenger>();

        CastHub hub = new(
            NullLogger<CastHub>.Instance,
            Mock.Of<IHttpContextAccessor>(),
            contextFactory,
            new ConnectedClients(),
            clientMessenger.Object,
            Mock.Of<IActivityLogger>(),
            Mock.Of<IAuthTokenStore>(),
            Mock.Of<IChromeCastService>()
        );

        ClaimsPrincipal principal = new(
            new ClaimsIdentity(
                [new(ClaimTypes.NameIdentifier, callerUserId.ToString())],
                "TestAuth"
            )
        );

        Mock<HubCallerContext> context = new();
        context.Setup(c => c.User).Returns(principal);
        context.Setup(c => c.ConnectionId).Returns(Guid.NewGuid().ToString());
        context.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

        hub.Context = context.Object;
        hub.Clients = Mock.Of<IHubCallerClients>();

        return hub;
    }

    public static IEnumerable<object[]> RoutedMethods()
    {
        yield return ["Play", (Func<CastHub, Task>)(h => h.Play())];
        yield return ["Pause", (Func<CastHub, Task>)(h => h.Pause())];
        yield return ["Time", (Func<CastHub, Task>)(h => h.Time(new CastHub.TimeData()))];
        yield return ["Ended", (Func<CastHub, Task>)(h => h.Ended())];
        yield return ["Volume", (Func<CastHub, Task>)(h => h.Volume(50))];
        yield return ["Muted", (Func<CastHub, Task>)(h => h.Muted(true))];
        yield return
        [
            "Item",
            (Func<CastHub, Task>)(h => h.Item(new CastHub.PlaylistItem { Id = "item-1" })),
        ];
        yield return ["Playlist", (Func<CastHub, Task>)(h => h.Playlist([]))];
        yield return ["SubtitleTracks", (Func<CastHub, Task>)(h => h.SubtitleTracks([]))];
        yield return
        [
            "CurrentSubtitleTrack",
            (Func<CastHub, Task>)(h => h.CurrentSubtitleTrack(new CastHub.TextTrack())),
        ];
        yield return ["AudioTracks", (Func<CastHub, Task>)(h => h.AudioTracks([]))];
        yield return
        [
            "CurrentAudioTrack",
            (Func<CastHub, Task>)(h => h.CurrentAudioTrack(new CastHub.AudioTrack())),
        ];
        yield return ["GetPlayerState", (Func<CastHub, Task>)(h => h.GetPlayerState())];
        yield return
        [
            // PlayerState the METHOD forwards a DIFFERENT event name than its own
            // name — this mapping is the entire point of the test.
            "MusicPlayerState",
            (Func<CastHub, Task>)(h => h.PlayerState(new CastHub.CastPlayerState())),
        ];
        yield return ["SetAudioTrack", (Func<CastHub, Task>)(h => h.SetAudioTrack(1))];
        yield return ["SetSubtitleTrack", (Func<CastHub, Task>)(h => h.SetSubtitleTrack(1))];
        yield return ["SetPlaylistItem", (Func<CastHub, Task>)(h => h.SetPlaylistItem(1))];
        yield return ["SetVolume", (Func<CastHub, Task>)(h => h.SetVolume(50))];
        yield return ["SetMuted", (Func<CastHub, Task>)(h => h.SetMuted(true))];
        yield return ["SetSeek", (Func<CastHub, Task>)(h => h.SetSeek(10))];
        yield return ["SetNext", (Func<CastHub, Task>)(h => h.SetNext())];
        yield return ["SetPrevious", (Func<CastHub, Task>)(h => h.SetPrevious())];
        yield return ["SetPlay", (Func<CastHub, Task>)(h => h.SetPlay())];
        yield return ["SetPause", (Func<CastHub, Task>)(h => h.SetPause())];
        yield return ["SetStop", (Func<CastHub, Task>)(h => h.SetStop())];
    }

    [Theory]
    [MemberData(nameof(RoutedMethods))]
    public async Task RoutedMethod_ForwardsExpectedEventName_ToCallerUser(
        string expectedEventName,
        Func<CastHub, Task> invoke
    )
    {
        CastHub hub = CreateHub(
            TestAuthHandler.DefaultUserId,
            out Mock<IClientMessenger> clientMessenger
        );

        await invoke(hub);

        clientMessenger.Verify(
            m =>
                m.SendTo(
                    expectedEventName,
                    "castHub",
                    TestAuthHandler.DefaultUserId,
                    It.IsAny<object?>()
                ),
            Times.Once
        );
    }

    [Theory]
    [MemberData(nameof(RoutedMethods))]
    public async Task RoutedMethod_IsNoOp_WhenCallerUserIsNotCached(
        string expectedEventName,
        Func<CastHub, Task> invoke
    )
    {
        _ = expectedEventName;
        CastHub hub = CreateHub(Guid.NewGuid(), out Mock<IClientMessenger> clientMessenger);

        await invoke(hub);

        clientMessenger.Verify(
            m =>
                m.SendTo(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<object?>()
                ),
            Times.Never
        );
    }
}
