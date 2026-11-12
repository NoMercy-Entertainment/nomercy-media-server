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
using Newtonsoft.Json;
using NoMercy.Api.Hubs;
using NoMercy.Api.Services.Cast;
using NoMercy.Api.WebSockets;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Encoder.Devices;
using NoMercy.Networking.Devices;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Messaging;
using NoMercy.Setup.Cast;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

// REQUIREMENT: a nearby (unregistered) Cast device found by id gets the server's
// own Cast launch, with the caller's play intent; anything else is refused.
[Trait("Category", "Characterization")]
public class DeviceHubNearbyCastTests : IClassFixture<NoMercyApiFactory>
{
    private const string CastId = "cast-test-id-1";
    private const string NearbyIp = "192.0.2.50";

    private readonly NoMercyApiFactory _factory;

    public DeviceHubNearbyCastTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _factory.CreateClient();
    }

    private sealed class RecordingWaker : IServerCastWaker
    {
        public List<(
            Device Tv,
            Guid UserId,
            CastIntent Intent,
            bool AndroidReceiver
        )> Calls
        { get; } = [];

        public Task<bool> WakeAsync(
            Device tv,
            Guid userId,
            CastIntent intent,
            bool useAndroidReceiver = true
        )
        {
            Calls.Add((tv, userId, intent, useAndroidReceiver));
            return Task.FromResult(true);
        }
    }

    private sealed class FixedRegistry(params CastMdnsHit[] hits) : ICastMdnsRegistry
    {
        public bool IsReachable(string? lanIp) => false;

        public IReadOnlyCollection<CastMdnsHit> GetSeen() => hits;
    }

    private DeviceHub CreateHub(Guid callerId, IServerCastWaker waker)
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        ICastMdnsRegistry registry = new FixedRegistry(
            new CastMdnsHit(CastId, "Test Room TV", "Test Cast", NearbyIp, DateTime.UtcNow)
        );
        DeviceBusRegistry busRegistry = new(
            new DeviceStateRepository(contextFactory),
            Mock.Of<IHubContext<DeviceHub>>(),
            registry
        );

        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/deviceHub";

        DeviceHub hub = new(
            new HttpContextAccessor { HttpContext = httpContext },
            contextFactory,
            _factory.GetConnectedClients(),
            busRegistry,
            new DeviceStateRepository(contextFactory),
            Mock.Of<Database.Activity.IActivityLogger>(),
            Mock.Of<IDeviceCapabilityRegistry>(),
            registry,
            waker,
            NullLogger<DeviceHub>.Instance
        );

        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, callerId.ToString())], "TestAuth")
        );
        Mock<HubCallerContext> context = new();
        context.Setup(c => c.User).Returns(principal);
        context.Setup(c => c.ConnectionId).Returns(Guid.NewGuid().ToString());
        hub.Context = context.Object;

        return hub;
    }

    private static NearbyCastRequest Request() =>
        new("tv", "placeholder-media-id", 842, "ja", "en", "full", "ass");

    [Fact]
    public async Task CastToNearby_KnownId_LaunchesWebReceiverWithCallersIntent()
    {
        RecordingWaker waker = new();
        DeviceHub hub = CreateHub(TestAuthHandler.DefaultUserId, waker);
        Ulid id = DeviceListComposer.DeterministicId(CastId);

        HubCommandResult result = await hub.CastToNearby(id.ToString(), Request());

        Assert.True(result.Ok);
        Assert.Equal("cast_sent", result.Status);
        (Device tv, Guid userId, CastIntent intent, bool androidReceiver) = Assert.Single(
            waker.Calls
        );
        Assert.Equal(NearbyIp, tv.LanIp);
        Assert.Equal(id, tv.Id);
        Assert.Equal(TestAuthHandler.DefaultUserId, userId);
        Assert.False(androidReceiver);
        Assert.Equal("play_video", intent.Type);
        Assert.Equal("tv", intent.MediaType);
        Assert.Equal("placeholder-media-id", intent.MediaId);
        Assert.Equal(842, intent.ResumeAt);
        Assert.Equal("ja", intent.AudioLanguage);
        Assert.Equal("en", intent.SubtitleLanguage);
        Assert.Equal("full", intent.SubtitleType);
        Assert.Equal("ass", intent.SubtitleCodec);
        Assert.Null(intent.ListType);
        Assert.Null(intent.ListId);
        Assert.Null(intent.TrackId);
        Assert.DoesNotContain("list_type", JsonConvert.SerializeObject(Request()));
        Assert.DoesNotContain("list_id", JsonConvert.SerializeObject(Request()));
        Assert.DoesNotContain("track_id", JsonConvert.SerializeObject(Request()));
    }

    [Fact]
    public async Task CastToNearby_SeasonAndEpisode_TravelInThePlayIntent()
    {
        RecordingWaker waker = new();
        DeviceHub hub = CreateHub(TestAuthHandler.DefaultUserId, waker);
        Ulid id = DeviceListComposer.DeterministicId(CastId);
        NearbyCastRequest request = JsonConvert.DeserializeObject<NearbyCastRequest>(
            """{"media_type":"tv","media_id":"placeholder-media-id","resume_at":90,"season":1,"episode":3}"""
        )!;

        HubCommandResult result = await hub.CastToNearby(id.ToString(), request);

        Assert.True(result.Ok);
        (Device _, Guid _, CastIntent intent, bool _) = Assert.Single(waker.Calls);
        Assert.Equal(1, intent.Season);
        Assert.Equal(3, intent.Episode);
    }

    [Fact]
    public async Task CastToNearby_MusicList_LaunchesPlayMusic()
    {
        RecordingWaker waker = new();
        DeviceHub hub = CreateHub(TestAuthHandler.DefaultUserId, waker);
        Ulid id = DeviceListComposer.DeterministicId(CastId);
        NearbyCastRequest request = JsonConvert.DeserializeObject<NearbyCastRequest>(
            """{"list_type":"playlist","list_id":"list-123","track_id":"track-4","resume_at":30}"""
        )!;

        HubCommandResult result = await hub.CastToNearby(id.ToString(), request);

        Assert.True(result.Ok);
        Assert.Equal("cast_sent", result.Status);
        (Device tv, Guid userId, CastIntent intent, bool androidReceiver) = Assert.Single(
            waker.Calls
        );
        Assert.Equal(id, tv.Id);
        Assert.Equal(NearbyIp, tv.LanIp);
        Assert.Equal(TestAuthHandler.DefaultUserId, userId);
        Assert.False(androidReceiver);
        Assert.Equal("play_music", intent.Type);
        Assert.Equal("playlist", intent.ListType);
        Assert.Equal("list-123", intent.ListId);
        Assert.Equal("track-4", intent.TrackId);
        Assert.Equal(30, intent.ResumeAt);
        Assert.Null(intent.MediaType);
        Assert.Null(intent.MediaId);
    }

    [Fact]
    public async Task CastToNearby_WithoutVideoOrMusic_IsInvalidInput()
    {
        RecordingWaker waker = new();
        DeviceHub hub = CreateHub(TestAuthHandler.DefaultUserId, waker);
        Ulid id = DeviceListComposer.DeterministicId(CastId);
        NearbyCastRequest request = JsonConvert.DeserializeObject<NearbyCastRequest>("{}")!;

        HubCommandResult result = await hub.CastToNearby(id.ToString(), request);

        Assert.False(result.Ok);
        Assert.Equal("invalid_input", result.ErrorCode);
        Assert.Empty(waker.Calls);
    }

    [Fact]
    public async Task CastToNearby_UnknownId_FailsWithoutLaunching()
    {
        RecordingWaker waker = new();
        DeviceHub hub = CreateHub(TestAuthHandler.DefaultUserId, waker);

        HubCommandResult result = await hub.CastToNearby(Ulid.NewUlid().ToString(), Request());

        Assert.False(result.Ok);
        Assert.Equal("not_found", result.ErrorCode);
        Assert.Empty(waker.Calls);
    }

    [Fact]
    public async Task CastToNearby_CallerNotInUserCache_IsRefused()
    {
        RecordingWaker waker = new();
        DeviceHub hub = CreateHub(Guid.NewGuid(), waker);
        Ulid id = DeviceListComposer.DeterministicId(CastId);

        HubCommandResult result = await hub.CastToNearby(id.ToString(), Request());

        Assert.False(result.Ok);
        Assert.Equal("not_owned", result.Status);
        Assert.Empty(waker.Calls);
    }

    [Fact]
    public async Task CastToNearby_BadDeviceId_IsInvalidInput()
    {
        RecordingWaker waker = new();
        DeviceHub hub = CreateHub(TestAuthHandler.DefaultUserId, waker);

        HubCommandResult result = await hub.CastToNearby("not-an-id", Request());

        Assert.Equal("invalid_input", result.ErrorCode);
        Assert.Empty(waker.Calls);
    }
}
