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
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Hubs;
using NoMercy.Api.Services.Music;
using NoMercy.Api.WebSockets;
using NoMercy.Authorization;
using NoMercy.Data.Activity;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.Setup.Cast;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// CrossfadeCompleteAtCommand through the real hub: the new track goes on at
/// the position the player names, a position that cannot be is refused, and
/// the older CrossfadeCompleteCommand keeps starting the new track at zero.
/// The same real-DI MusicHub harness MusicHubItemTaggedPositionTests uses.
/// </summary>
[Trait("Category", "Characterization")]
public class MusicHubCrossfadeCompleteAtTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public MusicHubCrossfadeCompleteAtTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _factory.CreateClient();
    }

    [Fact]
    public async Task The_new_track_goes_on_at_the_position_the_player_names()
    {
        Session session = SeedFadingSession();
        try
        {
            MusicHub hub = CreateHub(session.ConnectionId, session.UserId);

            HubCommandResult result = await hub.CrossfadeCompleteAtCommand(session.Next.Id, 42_000);

            result.Ok.Should().BeTrue();
            session.State.CurrentItem.Should().BeSameAs(session.Next);
            session.State.Time.Should().Be(42_000);
            session.State.IsCrossfading.Should().BeFalse();
        }
        finally
        {
            Cleanup(session);
        }
    }

    [Fact]
    public async Task The_older_command_still_starts_the_new_track_at_zero()
    {
        Session session = SeedFadingSession();
        try
        {
            MusicHub hub = CreateHub(session.ConnectionId, session.UserId);

            HubCommandResult result = await hub.CrossfadeCompleteCommand(session.Next.Id);

            result.Ok.Should().BeTrue();
            session.State.CurrentItem.Should().BeSameAs(session.Next);
            session.State.Time.Should().Be(0);
        }
        finally
        {
            Cleanup(session);
        }
    }

    [Fact]
    public async Task A_position_past_the_end_of_the_new_track_is_invalid_input()
    {
        Session session = SeedFadingSession();
        try
        {
            MusicHub hub = CreateHub(session.ConnectionId, session.UserId);

            HubCommandResult result = await hub.CrossfadeCompleteAtCommand(
                session.Next.Id,
                600_000
            );

            result.Ok.Should().BeFalse();
            result.ErrorCode.Should().Be("invalid_input");
            session.State.CurrentItem.Should().NotBeSameAs(session.Next);
        }
        finally
        {
            Cleanup(session);
        }
    }

    [Fact]
    public async Task A_missing_track_or_position_is_invalid_input()
    {
        Session session = SeedFadingSession();
        try
        {
            MusicHub hub = CreateHub(session.ConnectionId, session.UserId);

            HubCommandResult[] results =
            [
                await hub.CrossfadeCompleteAtCommand(null, 1000),
                await hub.CrossfadeCompleteAtCommand(Guid.Empty, 1000),
                await hub.CrossfadeCompleteAtCommand(session.Next.Id, null),
                await hub.CrossfadeCompleteAtCommand(session.Next.Id, -1),
            ];

            results.Should().OnlyContain(result => result.ErrorCode == "invalid_input");
            session.State.CurrentItem.Should().NotBeSameAs(session.Next);
        }
        finally
        {
            Cleanup(session);
        }
    }

    private sealed record Session(
        Guid UserId,
        User User,
        string ConnectionId,
        PlaylistTrackDto Next,
        MusicPlayerState State
    );

    private Session SeedFadingSession()
    {
        Guid userId = Guid.NewGuid();
        User user = new()
        {
            Id = userId,
            Email = $"{userId}@nomercy.tv",
            Name = "Crossfade Test User",
            Owner = false,
            Allowed = true,
            Manage = false,
        };
        UserCache.Current.AddUser(user);

        string connectionId = Guid.NewGuid().ToString();
        string deviceId = $"web-{Guid.NewGuid()}";
        Client client = new()
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = deviceId,
            Endpoint = "/musicHub",
            Type = "web",
            Socket = Mock.Of<ISingleClientProxy>(),
        };
        _factory.GetConnectedClients().Clients[connectionId] = client;

        PlaylistTrackDto next = MakeTrack();
        MusicPlayerState state = new()
        {
            DeviceId = deviceId,
            PlayState = true,
            CurrentItem = MakeTrack(),
            Playlist = [next],
            CurrentList = new("/music/albums/test", UriKind.Relative),
            Time = 178_000,
            LastActiveHeartbeatUtc = DateTime.UtcNow,
        };
        _factory.Services.GetRequiredService<MusicPlayerStateManager>().UpdateState(userId, state);
        _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>().Set(userId, client);
        _factory
            .Services.GetRequiredService<MusicPlaybackService>()
            .StartCrossfade(userId, deviceId, 3000);

        return new(userId, user, connectionId, next, state);
    }

    private void Cleanup(Session session)
    {
        _factory.GetConnectedClients().Clients.TryRemove(session.ConnectionId, out _);
        _factory.Services.GetRequiredService<MusicPlaybackService>().RemoveTimer(session.UserId);
        _factory.Services.GetRequiredService<MusicPlayerStateManager>().RemoveState(session.UserId);
        _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>().Remove(session.UserId);
        UserCache.Current.RemoveUser(session.User);
    }

    private static PlaylistTrackDto MakeTrack()
    {
        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Track",
            Duration = "180",
            Filename = "test.mp3",
            Folder = "/music/",
            FolderId = Ulid.NewUlid(),
        };
        return new(track, "US");
    }

    private MusicHub CreateHub(string connectionId, Guid userId)
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();

        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/musicHub";

        MusicHub hub = new(
            NullLogger<MusicHub>.Instance,
            new HttpContextAccessorStub(httpContext),
            contextFactory,
            _factory.GetConnectedClients(),
            _factory.Services.GetRequiredService<IClientMessenger>(),
            _factory.Services.GetRequiredService<MusicPlaybackService>(),
            _factory.Services.GetRequiredService<MusicPlayerStateManager>(),
            new(new MusicRepository(contextFactory)),
            _factory.Services.GetRequiredService<MusicPlaybackCommandHandler>(),
            Mock.Of<IActivityLogger>(),
            new(
                new DeviceStateRepository(contextFactory),
                Mock.Of<IHubContext<DeviceHub>>(),
                Mock.Of<ICastMdnsRegistry>()
            ),
            new(),
            Mock.Of<IChromeCastService>(),
            _factory.Services.GetRequiredService<CastPanelWakeLauncher>(),
            _factory.Services.GetRequiredService<MusicActiveDeviceRegistry>(),
            new DeviceStateRepository(contextFactory)
        );

        Mock<HubCallerContext> context = new();
        context
            .Setup(caller => caller.User)
            .Returns(
                new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new(ClaimTypes.NameIdentifier, userId.ToString())],
                        "TestAuth"
                    )
                )
            );
        context.Setup(caller => caller.ConnectionId).Returns(connectionId);
        context.Setup(caller => caller.ConnectionAborted).Returns(CancellationToken.None);

        Mock<IHubCallerClients> clients = new();
        clients.Setup(all => all.Caller).Returns(Mock.Of<ISingleClientProxy>());
        clients.Setup(all => all.User(It.IsAny<string>())).Returns(Mock.Of<ISingleClientProxy>());

        hub.Context = context.Object;
        hub.Clients = clients.Object;

        return hub;
    }

    private sealed class HttpContextAccessorStub(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}
