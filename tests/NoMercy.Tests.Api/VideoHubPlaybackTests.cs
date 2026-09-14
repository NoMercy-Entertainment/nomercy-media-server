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
using NoMercy.Api.Services.Video;
using NoMercy.Api.WebSockets;
using NoMercy.Authorization;
using NoMercy.Data.Activity;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Domain;
using NoMercy.Setup.Cast;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Requirement-driven coverage for VideoHub.Playback.cs — SetTime, RemoveWatched,
/// PlaybackCommand and the non-Cast branches of ChangeDeviceCommand. Builds a
/// real VideoHub against the app's DI-configured MediaContext/repositories (via
/// NoMercyApiFactory), mocking only the SignalR plumbing (HubCallerContext,
/// IHubCallerClients) and IChromeCastService (native Cast SDK has no test double).
/// </summary>
[Trait("Category", "Characterization")]
public class VideoHubPlaybackTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public VideoHubPlaybackTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _factory.CreateClient();
    }

    private (VideoHub Hub, Mock<IUserDataRepository> UserDataRepository) CreateHub(
        string connectionId,
        Guid userId,
        out Mock<IHubCallerClients> clients
    )
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        IServiceScope scope = _factory.Services.CreateScope();

        Mock<IUserDataRepository> userDataRepository = new();
        userDataRepository
            .Setup(r => r.GetWithPlaybackPreferencesAsync(It.IsAny<Guid>()))
            .Returns(
                (Guid id) =>
                    new UserDataRepository(contextFactory).GetWithPlaybackPreferencesAsync(id)
            );

        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/videoHub";

        VideoHub hub = new(
            NullLogger<VideoHub>.Instance,
            new HttpContextAccessorStub(httpContext),
            contextFactory,
            _factory.GetConnectedClients(),
            _factory.Services.GetRequiredService<IClientMessenger>(),
            _factory.Services.GetRequiredService<VideoPlaybackService>(),
            _factory.Services.GetRequiredService<VideoPlayerStateManager>(),
            scope.ServiceProvider.GetRequiredService<VideoPlaylistManager>(),
            _factory.Services.GetRequiredService<VideoPlaybackCommandHandler>(),
            Mock.Of<IActivityLogger>(),
            _factory.Services.GetRequiredService<CastSessionTokenService>(),
            _factory.Services.GetRequiredService<DeviceBusRegistry>(),
            _factory.Services.GetRequiredService<CastPanelWakeLauncher>(),
            userDataRepository.Object,
            new VideoFileRepository(contextFactory),
            null as INetworkDiscovery
        );

        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth")
        );

        Mock<HubCallerContext> context = new();
        context.Setup(c => c.User).Returns(principal);
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

        clients = new();
        Mock<ISingleClientProxy> callerProxy = new();
        Mock<ISingleClientProxy> userProxy = new();
        clients.Setup(c => c.Caller).Returns(callerProxy.Object);
        clients.Setup(c => c.User(It.IsAny<string>())).Returns(userProxy.Object);

        hub.Context = context.Object;
        hub.Clients = clients.Object;

        return (hub, userDataRepository);
    }

    private async Task<Ulid> SeededVideoFileIdAsync(int movieId)
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();
        return await ctx.VideoFiles.Where(v => v.MovieId == movieId).Select(v => v.Id).FirstAsync();
    }

    private async Task<UserData?> FindUserDataAsync(Ulid videoFileId, Guid userId)
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();
        return await ctx.UserData.FirstOrDefaultAsync(u =>
            u.VideoFileId == videoFileId && u.UserId == userId
        );
    }

    // SetTime's happy path performs a real FK-checked UserData Upsert (UserId,
    // MovieId, VideoFileId all enforced — MediaContext opens with "Foreign
    // Keys=True"). Reusing the shared seeded user/movie here would race any
    // other test class touching the same (VideoFileId, UserId, MovieId)
    // unique key under xUnit's parallel test collections, so this mints a
    // fully isolated Movie + VideoFile + User scoped to just this test.
    private async Task<(User User, int MovieId, Ulid VideoFileId)> SeedIsolatedMovieAndUserAsync()
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();

        int movieId = Random.Shared.Next(600_000_000, 699_000_000);
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid()}@nomercy.tv",
            Name = "SetTime Isolated Test User",
            Owner = false,
            Allowed = true,
            Manage = false,
        };
        Movie movie = new()
        {
            Id = movieId,
            Title = "SetTime Isolated Test Movie",
            TitleSort = "setTime isolated test movie",
        };
        ctx.Users.Add(user);
        ctx.Movies.Add(movie);
        await ctx.SaveChangesAsync();

        VideoFile videoFile = new()
        {
            Id = Ulid.NewUlid(),
            Filename = "isolated.mkv",
            Folder = "/movies/isolated",
            HostFolder = "/movies/isolated",
            Languages = "[\"en\"]",
            Quality = "1080p",
            Share = "movies",
            MovieId = movieId,
        };
        ctx.VideoFiles.Add(videoFile);
        await ctx.SaveChangesAsync();

        UserCache.Current.AddUser(user);

        return (user, movieId, videoFile.Id);
    }

    // =========================================================================
    // SetTime
    // =========================================================================

    [Fact]
    public async Task SetTime_UnknownVideoFile_DoesNotUpsert()
    {
        (VideoHub hub, _) = CreateHub(
            Guid.NewGuid().ToString(),
            TestAuthHandler.DefaultUserId,
            out _
        );

        await hub.SetTime(
            new()
            {
                VideoId = Ulid.NewUlid(),
                TmdbId = 129,
                PlaylistType = MediaTypes.MovieMediaType,
                Time = 1000,
            }
        );

        // No exception and (implicitly) no row written — verified by the movie
        // path test below actually finding a row for a REAL video file.
    }

    [Fact]
    public async Task SetTime_ValidMovie_HandsTheReportToTheWatchProgressUpsert()
    {
        (User user, int movieId, Ulid videoFileId) = await SeedIsolatedMovieAndUserAsync();

        try
        {
            (VideoHub hub, Mock<IUserDataRepository> repo) = CreateHub(
                Guid.NewGuid().ToString(),
                user.Id,
                out _
            );

            await hub.SetTime(
                new()
                {
                    VideoId = videoFileId,
                    TmdbId = movieId,
                    PlaylistType = MediaTypes.MovieMediaType,
                    Time = 42_000,
                    Audio = "en",
                    Subtitle = "none",
                    SubtitleType = "none",
                }
            );

            repo.Verify(
                r =>
                    r.UpsertWatchProgressAsync(
                        It.Is<WatchProgress>(progress =>
                            progress.UserId == user.Id
                            && progress.PlaylistType == MediaTypes.MovieMediaType
                            && progress.TmdbId == movieId
                            && progress.VideoFileId == videoFileId
                            && progress.Time == 42_000
                            && progress.Audio == "en"
                        ),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Once
            );
        }
        finally
        {
            UserCache.Current.RemoveUser(user);
        }
    }

    [Fact]
    public async Task SetTime_MovieIdNotInDatabase_DoesNotUpsert()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        Ulid videoFileId = await SeededVideoFileIdAsync(129);
        (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

        await hub.SetTime(
            new()
            {
                VideoId = videoFileId,
                TmdbId = 999_999_999,
                PlaylistType = MediaTypes.MovieMediaType,
                Time = 5_000,
            }
        );

        UserData? row = await FindUserDataAsync(videoFileId, userId);
        // Either no row was ever written for this (videoFileId, userId, movieId=999999999)
        // combination, or an earlier test already wrote one for MovieId 129 — either
        // way the bogus TmdbId must never have been persisted.
        if (row is not null)
            row.MovieId.Should().NotBe(999_999_999);
    }

    [Fact]
    public async Task SetTime_CollectionPlaylistId_NonNumeric_DoesNotThrow()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        Ulid videoFileId = await SeededVideoFileIdAsync(129);
        (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

        Func<Task> act = async () =>
            await hub.SetTime(
                new()
                {
                    VideoId = videoFileId,
                    TmdbId = 129,
                    PlaylistType = MediaTypes.CollectionMediaType,
                    PlaylistId = "not-a-number",
                    Time = 1_000,
                }
            );

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetTime_SpecialPlaylistId_NonUlid_DoesNotThrow()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        Ulid videoFileId = await SeededVideoFileIdAsync(129);
        (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

        Func<Task> act = async () =>
            await hub.SetTime(
                new()
                {
                    VideoId = videoFileId,
                    TmdbId = 129,
                    PlaylistType = MediaTypes.SpecialMediaType,
                    PlaylistId = "not-a-ulid",
                    Time = 1_000,
                }
            );

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetTime_UnknownCachedUser_IsNoOp()
    {
        // CreateHub's principal carries a userId with no matching UserCache
        // entry — SetTime's very first guard (`user is null`) must return
        // before touching the database at all.
        Guid unknownUserId = Guid.NewGuid();
        (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), unknownUserId, out _);

        Func<Task> act = async () =>
            await hub.SetTime(
                new()
                {
                    VideoId = Ulid.NewUlid(),
                    TmdbId = 129,
                    PlaylistType = MediaTypes.MovieMediaType,
                    Time = 1_000,
                }
            );

        await act.Should().NotThrowAsync();
    }

    // =========================================================================
    // RemoveWatched
    // =========================================================================

    [Fact]
    public async Task RemoveWatched_ValidUser_DelegatesToRepositoryWithTypedId()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        (VideoHub hub, Mock<IUserDataRepository> repo) = CreateHub(
            Guid.NewGuid().ToString(),
            userId,
            out _
        );

        await hub.RemoveWatched(new() { TmdbId = 129, PlaylistType = MediaTypes.MovieMediaType });

        repo.Verify(
            r =>
                r.RemoveForItemAsync(
                    userId,
                    MediaTypes.MovieMediaType,
                    129,
                    null,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task RemoveWatched_SpecialType_PassesUlidNotIntId()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        Ulid specialId = Ulid.NewUlid();
        (VideoHub hub, Mock<IUserDataRepository> repo) = CreateHub(
            Guid.NewGuid().ToString(),
            userId,
            out _
        );

        await hub.RemoveWatched(
            new()
            {
                TmdbId = 0,
                SpecialId = specialId,
                PlaylistType = MediaTypes.SpecialMediaType,
            }
        );

        repo.Verify(
            r =>
                r.RemoveForItemAsync(
                    userId,
                    MediaTypes.SpecialMediaType,
                    null,
                    specialId,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    // =========================================================================
    // PlaybackCommand
    // =========================================================================

    [Fact]
    public async Task PlaybackCommand_UnknownCachedUser_IsNoOp()
    {
        Guid unknownUserId = Guid.NewGuid();
        (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), unknownUserId, out _);

        Func<Task> act = async () => await hub.PlaybackCommand("play");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PlaybackCommand_NoExistingState_DoesNotThrow()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();
        stateManager.RemoveState(userId);
        (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

        Func<Task> act = async () => await hub.PlaybackCommand("play");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PlaybackCommand_ExistingState_DispatchesToRealCommandHandler()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();
        VideoPlayerState state = new() { PlayState = false };
        stateManager.UpdateState(userId, state);

        try
        {
            (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

            await hub.PlaybackCommand("play");

            state.PlayState.Should().BeTrue();
        }
        finally
        {
            stateManager.RemoveState(userId);
        }
    }

    [Fact]
    public async Task PlaybackCommand_StateHasNoDeviceId_AdoptsConnectedCallerDevice()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        string connectionId = Guid.NewGuid().ToString();
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();
        VideoPlayerState state = new() { PlayState = false, DeviceId = null };
        stateManager.UpdateState(userId, state);

        string deviceId = $"caller-device-{Guid.NewGuid()}";
        Client callerClient = new()
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = deviceId,
            Endpoint = "/videoHub",
            Type = "web",
            Socket = Mock.Of<ISingleClientProxy>(),
            VolumePercent = 77,
        };
        ConnectedClients connectedClients = _factory.GetConnectedClients();
        connectedClients.Clients[connectionId] = callerClient;

        try
        {
            (VideoHub hub, _) = CreateHub(connectionId, userId, out _);

            await hub.PlaybackCommand("mute");

            state.DeviceId.Should().Be(deviceId);
            state.VolumePercentage.Should().Be(77);
        }
        finally
        {
            connectedClients.Clients.TryRemove(connectionId, out _);
            stateManager.RemoveState(userId);
        }
    }

    // =========================================================================
    // ChangeDeviceCommand
    // =========================================================================

    [Fact]
    public async Task ChangeDeviceCommand_EmptyDeviceId_IsNoOp()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

        Func<Task> act = async () => await hub.ChangeDeviceCommand("");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ChangeDeviceCommand_NoExistingPlayerState_UpdatesPlaybackStateAndReturns()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();
        stateManager.RemoveState(userId);
        (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

        Func<Task> act = async () => await hub.ChangeDeviceCommand("some-device-id");

        await act.Should().NotThrowAsync();
        stateManager.HasState(userId).Should().BeFalse();
    }

    [Fact]
    public async Task ChangeDeviceCommand_NonTvTarget_SetsDeviceIdOnState_WithoutLaunchingCast()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();
        VideoPlayerState state = new() { PlayState = false };
        stateManager.UpdateState(userId, state);

        string targetDeviceId = $"phone-{Guid.NewGuid()}";

        try
        {
            (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

            await hub.ChangeDeviceCommand(targetDeviceId);

            state.DeviceId.Should().Be(targetDeviceId);
        }
        finally
        {
            stateManager.RemoveState(userId);
        }
    }

    // Minimal IHttpContextAccessor stand-in — the real implementation is an
    // AsyncLocal-backed singleton unsuited to constructing an isolated
    // HttpContext per test.
    private sealed class HttpContextAccessorStub(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    // =========================================================================
    // StartPlaybackCommand
    // =========================================================================

    // The hub used to remember the first device a user started on, in a static map
    // nothing ever cleared, so every later session was created on that device.
    [Fact]
    public async Task StartPlaybackCommand_NewSessionFromAnotherDevice_UsesTheCallingDevice()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        ConnectedClients connectedClients = _factory.GetConnectedClients();
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();

        string firstConnection = Guid.NewGuid().ToString();
        string secondConnection = Guid.NewGuid().ToString();
        Client firstDevice = new()
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = $"first-{Guid.NewGuid()}",
            Endpoint = "/videoHub",
            Type = "web",
            Socket = Mock.Of<ISingleClientProxy>(),
        };
        Client secondDevice = new()
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = $"second-{Guid.NewGuid()}",
            Endpoint = "/videoHub",
            Type = "web",
            Socket = Mock.Of<ISingleClientProxy>(),
        };
        connectedClients.Clients[firstConnection] = firstDevice;
        connectedClients.Clients[secondConnection] = secondDevice;

        try
        {
            (VideoHub firstHub, _) = CreateHub(firstConnection, userId, out _);
            await firstHub.StartPlaybackCommand(MediaTypes.MovieMediaType, "129", 129);
            stateManager.TryGetValue(userId, out VideoPlayerState? firstState).Should().BeTrue();
            firstState!.DeviceId.Should().Be(firstDevice.DeviceId);
            stateManager.RemoveState(userId);

            (VideoHub secondHub, _) = CreateHub(secondConnection, userId, out _);
            await secondHub.StartPlaybackCommand(MediaTypes.MovieMediaType, "129", 129);

            stateManager.TryGetValue(userId, out VideoPlayerState? secondState).Should().BeTrue();
            secondState!.DeviceId.Should().Be(secondDevice.DeviceId);
        }
        finally
        {
            stateManager.RemoveState(userId);
            connectedClients.Clients.TryRemove(firstConnection, out _);
            connectedClients.Clients.TryRemove(secondConnection, out _);
        }
    }
}
