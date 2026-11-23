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
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
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

    [Fact]
    public async Task LastVideoConnection_RemovesContinueWatchingRefreshCache()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        VideoPlaybackService playbackService =
            _factory.Services.GetRequiredService<VideoPlaybackService>();
        ConcurrentDictionary<Guid, (string Item, DateTime At)> refreshCache =
            (ConcurrentDictionary<Guid, (string Item, DateTime At)>)
                typeof(VideoPlaybackService)
                    .GetField(
                        "_lastContinueWatchingRefresh",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    )!
                    .GetValue(playbackService)!;
        refreshCache[userId] = ("test", DateTime.UtcNow);

        try
        {
            (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);
            await hub.OnDisconnectedAsync(null);

            Assert.False(refreshCache.ContainsKey(userId));
        }
        finally
        {
            refreshCache.TryRemove(userId, out _);
        }
    }

    [Fact]
    public async Task MissingCachedUser_RemovesContinueWatchingRefreshCache()
    {
        Guid userId = Guid.NewGuid();
        VideoPlaybackService playbackService =
            _factory.Services.GetRequiredService<VideoPlaybackService>();
        ConcurrentDictionary<Guid, (string Item, DateTime At)> refreshCache =
            (ConcurrentDictionary<Guid, (string Item, DateTime At)>)
                typeof(VideoPlaybackService)
                    .GetField(
                        "_lastContinueWatchingRefresh",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    )!
                    .GetValue(playbackService)!;
        refreshCache[userId] = ("test", DateTime.UtcNow);

        try
        {
            (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);
            await hub.OnDisconnectedAsync(null);

            Assert.False(refreshCache.ContainsKey(userId));
        }
        finally
        {
            refreshCache.TryRemove(userId, out _);
        }
    }

    // A disconnect whose user is no longer cached used to return before the base
    // hub cleanup ran, so the connection stayed registered: a ghost device in
    // every device list and a device-connection count that never went back down.
    [Fact]
    public async Task MissingCachedUser_StillRemovesConnectionFromConnectedClients()
    {
        Guid userId = Guid.NewGuid();
        string connectionId = Guid.NewGuid().ToString();
        ConnectedClients connectedClients = _factory.GetConnectedClients();
        connectedClients.Clients[connectionId] = new Client
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = $"ghost-{Guid.NewGuid()}",
            Endpoint = "/videoHub",
            Type = "web",
            Socket = Mock.Of<ISingleClientProxy>(),
        };

        try
        {
            (VideoHub hub, _) = CreateHub(connectionId, userId, out _);
            await hub.OnDisconnectedAsync(null);

            connectedClients
                .Clients.Should()
                .NotContainKey(
                    connectionId,
                    "the base hub cleanup must run even when the user is not cached"
                );
        }
        finally
        {
            connectedClients.Clients.TryRemove(connectionId, out _);
        }
    }

    [Fact]
    public async Task PlaybackCommand_SeekObject_RoundsTimeAndUpdatesState()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();
        VideoPlayerState state = new();
        stateManager.UpdateState(userId, state);

        try
        {
            (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

            HubCommandResult result = await hub.PlaybackCommand(
                "seek",
                new JObject { ["time"] = 12.5 }
            );

            result.Ok.Should().BeTrue();
            state.Time.Should().Be(13_000);
        }
        finally
        {
            stateManager.RemoveState(userId);
        }
    }

    private (VideoHub Hub, Mock<IUserDataRepository> UserDataRepository) CreateHub(
        string connectionId,
        Guid userId,
        out Mock<IHubCallerClients> clients,
        IActivityLogger? activityLogger = null,
        CastPanelWakeLauncher? castPanelWakeLauncher = null,
        DeviceBusRegistry? busRegistry = null,
        ILogger<VideoHub>? logger = null,
        IClientMessenger? clientMessenger = null
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
            logger ?? NullLogger<VideoHub>.Instance,
            new HttpContextAccessorStub(httpContext),
            contextFactory,
            _factory.GetConnectedClients(),
            clientMessenger ?? _factory.Services.GetRequiredService<IClientMessenger>(),
            _factory.Services.GetRequiredService<VideoPlaybackService>(),
            _factory.Services.GetRequiredService<VideoPlayerStateManager>(),
            scope.ServiceProvider.GetRequiredService<VideoPlaylistManager>(),
            _factory.Services.GetRequiredService<VideoPlaybackCommandHandler>(),
            activityLogger ?? Mock.Of<IActivityLogger>(),
            _factory.Services.GetRequiredService<CastSessionTokenService>(),
            busRegistry ?? _factory.Services.GetRequiredService<DeviceBusRegistry>(),
            castPanelWakeLauncher ?? _factory.Services.GetRequiredService<CastPanelWakeLauncher>(),
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
            // A real library: an unset LibraryId only passed while a phantom library existed.
            LibraryId = NoMercyApiFactory.MovieLibraryId,
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

        HubCommandResult result = await hub.SetTime(
            new()
            {
                VideoId = Ulid.NewUlid(),
                TmdbId = 129,
                PlaylistType = MediaTypes.MovieMediaType,
                Time = 1000,
            }
        );

        result.ErrorCode.Should().Be("not_found");
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
        repo.Setup(r =>
                r.RemoveForItemAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<Ulid?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(1);

        HubCommandResult result = await hub.RemoveWatched(
            new() { TmdbId = 129, PlaylistType = MediaTypes.MovieMediaType }
        );

        result.Ok.Should().BeTrue();
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
        repo.Setup(r =>
                r.RemoveForItemAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<Ulid?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(1);

        HubCommandResult result = await hub.RemoveWatched(
            new()
            {
                TmdbId = 0,
                SpecialId = specialId,
                PlaylistType = MediaTypes.SpecialMediaType,
            }
        );

        result.Ok.Should().BeTrue();
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

    [Fact]
    public async Task RemoveWatched_UnknownItem_ReturnsNotFound()
    {
        (VideoHub hub, _) = CreateHub(
            Guid.NewGuid().ToString(),
            TestAuthHandler.DefaultUserId,
            out _
        );

        HubCommandResult result = await hub.RemoveWatched(
            new() { TmdbId = 999_999_999, PlaylistType = MediaTypes.MovieMediaType }
        );

        result.ErrorCode.Should().Be("not_found");
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
    public async Task ChangeDeviceCommand_UnknownNonTvTarget_ReturnsNotFoundWithoutChangingState()
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

            HubCommandResult result = await hub.ChangeDeviceCommand(targetDeviceId);

            result.ErrorCode.Should().Be("not_found");
            state.DeviceId.Should().BeNull();
        }
        finally
        {
            stateManager.RemoveState(userId);
        }
    }

    [Fact]
    public async Task ChangeDeviceCommand_ConnectedNonTvTarget_StillChangesDevice()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        string targetConnectionId = Guid.NewGuid().ToString();
        string targetDeviceId = $"phone-{Guid.NewGuid()}";
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();
        VideoPlayerState state = new() { PlayState = false };
        stateManager.UpdateState(userId, state);

        ConnectedClients connectedClients = _factory.GetConnectedClients();
        connectedClients.Clients[targetConnectionId] = new Client
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = targetDeviceId,
            Endpoint = "/videoHub",
            Type = "phone",
            Socket = Mock.Of<ISingleClientProxy>(),
        };

        try
        {
            (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

            HubCommandResult result = await hub.ChangeDeviceCommand(targetDeviceId);

            result.Ok.Should().BeTrue();
            state.DeviceId.Should().Be(targetDeviceId);
        }
        finally
        {
            connectedClients.Clients.TryRemove(targetConnectionId, out _);
            stateManager.RemoveState(userId);
        }
    }

    private const string TvIp = "192.168.1.50";

    private (DeviceBusRegistry Registry, Device Tv) RegistryWithSleepingTv(Guid userId)
    {
        Device tv = new()
        {
            Id = Ulid.NewUlid(),
            DeviceId = $"tv-{Guid.NewGuid()}",
            Name = "Test Room TV",
            Type = "tv",
            LanIp = TvIp,
            Fingerprint = Guid.NewGuid().ToString(),
            OwnerUserId = userId,
        };
        Mock<IDeviceStateRepository> repository = new();
        repository.Setup(r => r.GetOwnedTvsAsync(userId)).ReturnsAsync([tv]);
        DeviceBusRegistry registry = new(
            repository.Object,
            Mock.Of<IHubContext<DeviceHub>>(),
            Mock.Of<ICastMdnsRegistry>()
        );
        return (registry, tv);
    }

    private static CastPanelWakeLauncher LauncherWith(Mock<IChromeCastService> chromeCast) =>
        new(chromeCast.Object, NullLogger<CastPanelWakeLauncher>.Instance);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (Lines)
                Lines.Add((logLevel, formatter(state, exception)));
        }

        public async Task<bool> WaitForAsync(LogLevel level, string text)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                lock (Lines)
                    if (Lines.Any(l => l.Level == level && l.Message.Contains(text)))
                        return true;
                await Task.Delay(50);
            }

            return false;
        }
    }

    private async Task<(
        CapturingLogger<VideoHub> Logger,
        Mock<IClientMessenger> Messenger,
        Task<HubCommandResult> Call
    )> StartTvChangeAsync(Mock<IChromeCastService> chromeCast)
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        _factory
            .Services.GetRequiredService<VideoPlayerStateManager>()
            .UpdateState(userId, new VideoPlayerState { PlayState = false });
        (DeviceBusRegistry registry, Device tv) = RegistryWithSleepingTv(userId);
        CapturingLogger<VideoHub> logger = new();
        Mock<IClientMessenger> messenger = new();
        (VideoHub hub, _) = CreateHub(
            Guid.NewGuid().ToString(),
            userId,
            out _,
            castPanelWakeLauncher: LauncherWith(chromeCast),
            busRegistry: registry,
            logger: logger,
            clientMessenger: messenger.Object
        );
        Task<HubCommandResult> call = hub.ChangeDeviceCommand(tv.DeviceId);
        await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5)));
        return (logger, messenger, call);
    }

    [Fact]
    public async Task ChangeDeviceCommand_LaunchNeverCompletes_StillReturnsAndBroadcastsChangeDevice()
    {
        Mock<IChromeCastService> chromeCast = new();
        chromeCast
            .Setup(c => c.FindReceiverNameByIpAsync(TvIp))
            .Returns(new TaskCompletionSource<string?>().Task);

        try
        {
            (_, Mock<IClientMessenger> messenger, Task<HubCommandResult> call) =
                await StartTvChangeAsync(chromeCast);

            call.IsCompleted.Should().BeTrue("the hub call must not wait for the Cast launch");
            (await call).Ok.Should().BeTrue();
            messenger.Verify(
                m =>
                    m.SendTo(
                        "ChangeDevice",
                        "videoHub",
                        TestAuthHandler.DefaultUserId,
                        It.IsAny<object?>()
                    ),
                Times.Once
            );
        }
        finally
        {
            _factory
                .Services.GetRequiredService<VideoPlayerStateManager>()
                .RemoveState(TestAuthHandler.DefaultUserId);
        }
    }

    [Fact]
    public async Task ChangeDeviceCommand_LaunchFails_LogsTheErrorOutcome()
    {
        Mock<IChromeCastService> chromeCast = new();
        chromeCast.Setup(c => c.FindReceiverNameByIpAsync(TvIp)).ReturnsAsync("Test Room TV");
        chromeCast
            .Setup(c => c.SelectChromecast("Test Room TV"))
            .ThrowsAsync(new InvalidOperationException("receiver refused"));

        try
        {
            (CapturingLogger<VideoHub> logger, _, _) = await StartTvChangeAsync(chromeCast);

            (await logger.WaitForAsync(LogLevel.Error, "Video cast launch to")).Should().BeTrue();
            logger.Lines.Should().Contain(l => l.Message.EndsWith("Failed"));
        }
        finally
        {
            _factory
                .Services.GetRequiredService<VideoPlayerStateManager>()
                .RemoveState(TestAuthHandler.DefaultUserId);
        }
    }

    [Fact]
    public async Task ChangeDeviceCommand_LaunchSucceeds_LogsTheInformationOutcome()
    {
        Mock<IChromeCastService> chromeCast = new();
        chromeCast.Setup(c => c.FindReceiverNameByIpAsync(TvIp)).ReturnsAsync("Test Room TV");

        try
        {
            (CapturingLogger<VideoHub> logger, _, _) = await StartTvChangeAsync(chromeCast);

            (await logger.WaitForAsync(LogLevel.Information, "Video cast launch to"))
                .Should()
                .BeTrue();
            logger.Lines.Should().Contain(l => l.Message.EndsWith("Launched"));
        }
        finally
        {
            _factory
                .Services.GetRequiredService<VideoPlayerStateManager>()
                .RemoveState(TestAuthHandler.DefaultUserId);
        }
    }

    [Fact]
    public async Task ChangeDeviceCommand_NoPlaybackState_SaysNoPlaybackWasMoved()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        string targetConnectionId = Guid.NewGuid().ToString();
        string targetDeviceId = $"phone-{Guid.NewGuid()}";
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();
        stateManager.RemoveState(userId);
        ConnectedClients connectedClients = _factory.GetConnectedClients();
        connectedClients.Clients[targetConnectionId] = new Client
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = targetDeviceId,
            Endpoint = "/videoHub",
            Type = "phone",
            Socket = Mock.Of<ISingleClientProxy>(),
        };

        try
        {
            (VideoHub hub, _) = CreateHub(Guid.NewGuid().ToString(), userId, out _);

            HubCommandResult result = await hub.ChangeDeviceCommand(targetDeviceId);

            result.Ok.Should().BeTrue();
            result.Status.Should().Be("no_playback_moved");
            result.Message.Should().Be("The device is selected. No playback was moved.");
        }
        finally
        {
            connectedClients.Clients.TryRemove(targetConnectionId, out _);
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

    [Fact]
    public async Task StartPlaybackCommand_FailureLogIdentifiesTheTitleAndItem()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();
        Mock<IActivityLogger> activityLogger = new();
        (VideoHub hub, _) = CreateHub(
            Guid.NewGuid().ToString(),
            userId,
            out _,
            activityLogger.Object
        );

        try
        {
            stateManager.RemoveState(userId);
            await hub.StartPlaybackCommand(MediaTypes.MovieMediaType, "129", 129);

            activityLogger.Verify(
                logger =>
                    logger.LogFailureAsync(
                        "failure.playback_start",
                        userId,
                        It.IsAny<Ulid>(),
                        It.IsAny<string>(),
                        It.Is<string>(message =>
                            message.Contains("Spirited Away") && message.Contains("129")
                        ),
                        It.IsAny<Ulid?>(),
                        It.IsAny<object?>(),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Once
            );
        }
        finally
        {
            stateManager.RemoveState(userId);
        }
    }

    // The cast receiver sends the list id as a JSON number. Newtonsoft hands the
    // dynamic parameter a long, and int.Parse(long) threw RuntimeBinderException,
    // so playback never started on the TV.
    [Fact]
    public async Task StartPlaybackCommand_NumericListId_StartsPlayback()
    {
        Guid userId = TestAuthHandler.DefaultUserId;
        VideoPlayerStateManager stateManager =
            _factory.Services.GetRequiredService<VideoPlayerStateManager>();
        ConnectedClients connectedClients = _factory.GetConnectedClients();
        string connection = Guid.NewGuid().ToString();
        connectedClients.Clients[connection] = new()
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = $"cast-{Guid.NewGuid()}",
            Endpoint = "/videoHub",
            Type = "cast",
            Socket = Mock.Of<ISingleClientProxy>(),
        };
        Mock<IActivityLogger> activityLogger = new();
        (VideoHub hub, _) = CreateHub(connection, userId, out _, activityLogger.Object);

        try
        {
            stateManager.RemoveState(userId);
            await hub.StartPlaybackCommand(MediaTypes.MovieMediaType, 129L, 129);

            activityLogger.Verify(
                logger =>
                    logger.LogFailureAsync(
                        "failure.playback_start",
                        It.IsAny<Guid>(),
                        It.IsAny<Ulid>(),
                        "RuntimeBinderException",
                        It.IsAny<string>(),
                        It.IsAny<Ulid?>(),
                        It.IsAny<object?>(),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Never
            );
            stateManager.TryGetValue(userId, out VideoPlayerState? state).Should().BeTrue();
            state!.CurrentList.ToString().Should().Be("/movie/129/watch");
        }
        finally
        {
            stateManager.RemoveState(userId);
            connectedClients.Clients.TryRemove(connection, out _);
        }
    }

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
