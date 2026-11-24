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
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Api.Services.Cast;
using NoMercy.Api.Services.Music;
using NoMercy.Api.Services.Video;
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
using Xunit.Abstractions;

namespace NoMercy.Tests.Api;

// REQUIREMENT (cast session model, .claude/work/cast-unified/model-2026-11-22.md): one session
// per user and media kind, one active device, and the same rules for video and music whoever
// opened the session. Real hubs over real SignalR clients; devices are clients with their own
// client_id. A red row is a gap between today's hub and the model (Part 7).
[Trait("Category", "Integration")]
public class CastSessionModelTests(NoMercyApiFactory factory, ITestOutputHelper output)
    : IClassFixture<NoMercyApiFactory>,
        IAsyncLifetime
{
    public enum Kind
    {
        Video,
        Music,
    }

    private const int MovieId = 129;
    private const int RandomSteps = 20;

    private static readonly Guid UserId = TestAuthHandler.DefaultUserId;

    private readonly List<Actor> _actors = [];

    private sealed record SessionView(string? Active, bool HasItem, int TimeMs);

    private sealed class Actor(string name, string deviceId)
    {
        public string Name { get; } = name;
        public string DeviceId { get; } = deviceId;
        public HubConnection? Connection { get; set; }
        public bool Connected => Connection?.State == HubConnectionState.Connected;
    }

    public async Task InitializeAsync()
    {
        factory.CreateClient();
        IDbContextFactory<MediaContext> contextFactory = factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        if (!await context.LibraryUser.AnyAsync(l => l.LibraryId == NoMercyApiFactory.MusicLibraryId && l.UserId == UserId))
        {
            context.LibraryUser.Add(new(NoMercyApiFactory.MusicLibraryId, UserId));
            await context.SaveChangesAsync();
        }
        ClearSessions();
    }

    public async Task DisposeAsync()
    {
        foreach (Actor actor in _actors)
            if (actor.Connection is not null)
                await actor.Connection.DisposeAsync();

        await SettleAsync(
            () =>
                !factory
                    .GetConnectedClients()
                    .Clients.Values.Any(c =>
                        c.DeviceId.StartsWith("cast-model-", StringComparison.Ordinal)
                    ),
            TimeSpan.FromSeconds(3)
        );
        ClearSessions();
    }

    private void ClearSessions()
    {
        factory.Services.GetRequiredService<VideoPlayerStateManager>().RemoveState(UserId);
        factory.Services.GetRequiredService<MusicPlayerStateManager>().RemoveState(UserId);
        factory.Services.GetRequiredService<MusicActiveDeviceRegistry>().Remove(UserId);
    }

    private static string HubName(Kind kind) => kind == Kind.Video ? "videoHub" : "musicHub";

    private async Task<Actor> ConnectAsync(Kind kind, string name)
    {
        Actor? actor = _actors.FirstOrDefault(a => a.Name == name);
        if (actor is null)
        {
            actor = new(name, $"cast-model-{name}-{Guid.NewGuid():N}");
            _actors.Add(actor);
        }

        actor.Connection = await HubTestConnection.ConnectAsync(
            factory,
            $"{HubName(kind)}?client_id={actor.DeviceId}&client_type=web&client_name={actor.Name}"
        );
        (await SettleAsync(
            () =>
                factory
                    .GetConnectedClients()
                    .Clients.Values.Any(c =>
                        c.DeviceId == actor.DeviceId && c.Endpoint == "/" + HubName(kind)
                    ),
            TimeSpan.FromSeconds(5)
        ))
            .Should()
            .BeTrue("the hub registers the client right after the handshake");
        return actor;
    }

    private async Task DisconnectAsync(Actor actor)
    {
        if (actor.Connection is null)
            return;
        await actor.Connection.StopAsync();
        await actor.Connection.DisposeAsync();
        actor.Connection = null;
        await SettleAsync(
            () =>
                !factory
                    .GetConnectedClients()
                    .Clients.Values.Any(c => c.DeviceId == actor.DeviceId),
            TimeSpan.FromSeconds(3)
        );
    }

    private static async Task<bool> SettleAsync(Func<bool> condition, TimeSpan limit)
    {
        DateTime deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private static async Task<JsonElement> InvokeAsync(
        Actor caller,
        string method,
        params object?[] args
    )
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        return await caller.Connection!.InvokeCoreAsync<JsonElement>(
            method,
            args,
            timeout.Token
        );
    }

    private static Task<JsonElement> ChangeDeviceAsync(Actor caller, Actor target) =>
        InvokeAsync(caller, "ChangeDeviceCommand", target.DeviceId);

    private static async Task<JsonElement> StartAsync(Kind kind, Actor caller)
    {
        JsonElement result =
            kind == Kind.Video
                ? await InvokeAsync(
                    caller,
                    "StartPlaybackCommand",
                    "movie",
                    MovieId.ToString(),
                    MovieId
                )
                : await InvokeAsync(
                    caller,
                    "StartPlaybackCommand",
                    "album",
                    NoMercyApiFactory.AlbumId1,
                    NoMercyApiFactory.TrackId1
                );
        result
            .GetProperty("ok")
            .GetBoolean()
            .Should()
            .BeTrue($"the start itself must be accepted: {result}");
        return result;
    }

    private async Task<JsonElement> ReportTimeAsync(Kind kind, Actor caller, int seconds)
    {
        if (kind == Kind.Music)
            return await InvokeAsync(caller, "CurrentTimeCommand", seconds);

        IDbContextFactory<MediaContext> contextFactory = factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        Ulid videoId = await context
            .VideoFiles.Where(v => v.MovieId == MovieId)
            .Select(v => v.Id)
            .FirstAsync();
        return await InvokeAsync(
            caller,
            "SetTime",
            new Dictionary<string, object?>
            {
                ["video_id"] = videoId.ToString(),
                ["tmdb_id"] = MovieId,
                ["playlist_type"] = "movie",
                ["playlist_id"] = MovieId.ToString(),
                ["time"] = seconds,
                ["audio"] = "",
                ["subtitle"] = "",
                ["subtitle_type"] = "",
            }
        );
    }

    private static Task<JsonElement> SeekAsync(Actor caller, int seconds) =>
        InvokeAsync(caller, "PlaybackCommand", "seek", seconds);

    private SessionView View(Kind kind)
    {
        if (kind == Kind.Video)
        {
            VideoPlayerState? state = factory
                .Services.GetRequiredService<VideoPlayerStateManager>()
                .GetState(UserId);
            return state is null
                ? new(null, false, 0)
                : new(state.DeviceId, state.CurrentItem is not null, state.Time);
        }

        MusicPlayerState? music = factory
            .Services.GetRequiredService<MusicPlayerStateManager>()
            .GetState(UserId);
        string? registered = factory
            .Services.GetRequiredService<MusicActiveDeviceRegistry>()
            .TryGet(UserId, out Device? device)
            ? device.DeviceId
            : null;
        return new(music?.DeviceId ?? registered, music?.CurrentItem is not null, music?.Time ?? 0);
    }

    private string? MusicRegistryDevice()
    {
        return factory
            .Services.GetRequiredService<MusicActiveDeviceRegistry>()
            .TryGet(UserId, out Device? device)
            ? device.DeviceId
            : null;
    }

    private async Task<(Actor Phone, Actor Tv)> TwoDevicesAsync(Kind kind)
    {
        Actor phone = await ConnectAsync(kind, "phone");
        Actor tv = await ConnectAsync(kind, "tv");
        return (phone, tv);
    }

    // a1 Claim, S0 -> S1: the profile-menu pick of a device with nothing playing.
    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task Claim_WithNoPlayback_MakesTheTargetTheActiveDevice(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);

        await ChangeDeviceAsync(phone, tv);

        View(kind).Active.Should().Be(tv.DeviceId);
    }

    // R1 / I17: a play press on a sender inside a claimed session goes to the active device.
    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task Start_InsideAClaimedSession_PlaysOnTheActiveDeviceNotTheSender(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);
        await ChangeDeviceAsync(phone, tv);

        await StartAsync(kind, phone);

        SessionView view = View(kind);
        view.HasItem.Should().BeTrue();
        view.Active.Should().Be(tv.DeviceId);
    }

    // R1 from the other origin: the TV itself holds the session, a third device presses play.
    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task Start_FromAPassiveDeviceWhileAnotherPlays_DoesNotStealTheSession(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);
        await StartAsync(kind, tv);

        await StartAsync(kind, phone);

        View(kind).Active.Should().Be(tv.DeviceId);
    }

    // I12: a Join (a18) never changes the session.
    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task Join_OfANewDevice_LeavesTheSessionUnchanged(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);
        await ChangeDeviceAsync(phone, tv);
        await StartAsync(kind, phone);
        SessionView before = View(kind);

        await ConnectAsync(kind, "browser");

        View(kind).Should().Be(before);
    }

    // I4: the same start to the device that already plays it changes nothing (no seek back).
    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task Start_SecondIdenticalStartToTheActiveDevice_IsANoOp(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);
        await StartAsync(kind, tv);
        await SeekAsync(tv, 120);
        SessionView before = View(kind);
        before.TimeMs.Should().Be(120_000, "the seek is the precondition of this row");

        await StartAsync(kind, tv);

        View(kind).Should().Be(before);
    }

    // a9 Transfer, phone -> TV, the phone started the session.
    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task Transfer_FromThePhoneThatStarted_MovesToTheTv(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);
        await StartAsync(kind, phone);
        View(kind).Active.Should().Be(phone.DeviceId);

        await ChangeDeviceAsync(phone, tv);

        View(kind).Active.Should().Be(tv.DeviceId);
        View(kind).HasItem.Should().BeTrue();
    }

    // a9 Transfer, TV -> phone, the session was opened by the TV side.
    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task Transfer_FromTheTvThatStarted_MovesToThePhone(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);
        await StartAsync(kind, tv);
        View(kind).Active.Should().Be(tv.DeviceId);

        await ChangeDeviceAsync(tv, phone);

        View(kind).Active.Should().Be(phone.DeviceId);
        View(kind).HasItem.Should().BeTrue();
    }

    // a9 Transfer asked by a device that is neither the source nor the target.
    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task Transfer_AskedByAThirdDevice_MovesPlaybackToTheNamedTarget(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);
        Actor browser = await ConnectAsync(kind, "browser");
        await StartAsync(kind, tv);

        await ChangeDeviceAsync(browser, phone);

        View(kind).Active.Should().Be(phone.DeviceId);
    }

    // I12 / I7 / I8: a passive device that reports time never becomes active and never moves the clock.
    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task TimeReport_FromAPassiveDevice_NeverBecomesActive(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);
        await StartAsync(kind, tv);

        await ReportTimeAsync(kind, phone, 300);

        View(kind).Active.Should().Be(tv.DeviceId);
    }

    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task TimeReport_FromAPassiveDevice_DoesNotMoveTheActiveClock(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);
        await StartAsync(kind, tv);
        await SeekAsync(tv, 60);
        SessionView before = View(kind);
        before.TimeMs.Should().Be(60_000, "the seek is the precondition of this row");

        await ReportTimeAsync(kind, phone, 300);

        View(kind).TimeMs.Should().Be(60_000);
    }

    // I13: when the active device goes away, it is not the active device for ever.
    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task ActiveDeviceDisconnect_DoesNotLeaveAGhostActiveDevice(Kind kind)
    {
        (Actor phone, Actor tv) = await TwoDevicesAsync(kind);
        await ChangeDeviceAsync(phone, tv);
        await StartAsync(kind, phone);
        View(kind).Active.Should().Be(tv.DeviceId, "the TV must hold the session before it drops");

        await DisconnectAsync(tv);
        await SettleAsync(() => View(kind).Active != tv.DeviceId, TimeSpan.FromSeconds(3));

        View(kind).Active.Should().NotBe(tv.DeviceId);
    }

    private sealed class RecordingWaker : IServerCastWaker
    {
        public List<CastIntent> Intents { get; } = [];

        public Task<bool> WakeAsync(
            Device tv,
            Guid userId,
            CastIntent intent,
            string? callerBearer,
            bool useAndroidReceiver = true
        )
        {
            Intents.Add(intent);
            return Task.FromResult(true);
        }
    }

    // I11 / a19: a wake for video carries no load data and starts no playback, in either kind.
    [Fact]
    public async Task WakeForVideo_WithNoLoadData_StartsNothing()
    {
        IDbContextFactory<MediaContext> contextFactory = factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        Device tv = new()
        {
            Id = Ulid.NewUlid(),
            DeviceId = $"cast-model-wake-{Guid.NewGuid():N}",
            Name = "Model Wake TV",
            Type = "tv",
            LanIp = "192.0.2.77",
            Fingerprint = $"fp-{Guid.NewGuid()}",
            OwnerUserId = UserId,
        };
        await using (MediaContext seed = await contextFactory.CreateDbContextAsync())
        {
            seed.Devices.Add(tv);
            await seed.SaveChangesAsync();
        }

        RecordingWaker waker = new();
        DeviceHub hub = CreateDeviceHub(contextFactory, waker);

        HubCommandResult result = await hub.WakeForVideo(tv.Id.ToString());

        result.Ok.Should().BeTrue();
        waker.Intents.Should().ContainSingle();
        waker.Intents[0].Type.Should().Be("idle");
        waker.Intents[0].MediaId.Should().BeNull();
        View(Kind.Video).Should().Be(new SessionView(null, false, 0));
        View(Kind.Music).Should().Be(new SessionView(null, false, 0));
    }

    private DeviceHub CreateDeviceHub(
        IDbContextFactory<MediaContext> contextFactory,
        IServerCastWaker waker
    )
    {
        Mock<ICastMdnsRegistry> registry = new();
        DeviceBusRegistry busRegistry = new(
            new DeviceStateRepository(contextFactory),
            Mock.Of<IHubContext<DeviceHub>>(),
            registry.Object
        );
        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/deviceHub";
        DeviceHub hub = new(
            new HttpContextAccessor { HttpContext = httpContext },
            contextFactory,
            factory.GetConnectedClients(),
            busRegistry,
            new DeviceStateRepository(contextFactory),
            Mock.Of<Database.Activity.IActivityLogger>(),
            Mock.Of<IDeviceCapabilityRegistry>(),
            registry.Object,
            waker,
            NullLogger<DeviceHub>.Instance
        );
        Mock<HubCallerContext> context = new();
        context
            .Setup(c => c.User)
            .Returns(
                new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new(ClaimTypes.NameIdentifier, UserId.ToString())],
                        "TestAuth"
                    )
                )
            );
        context.Setup(c => c.ConnectionId).Returns(Guid.NewGuid().ToString());
        hub.Context = context.Object;
        return hub;
    }

    // Reducer oracle for the random-order test: the model of Part 2 reduced to what the
    // hubs expose (active device, item present, clock).
    private sealed class Model
    {
        public string? Active { get; private set; }
        public bool Item { get; private set; }
        public int? TimeMs { get; private set; }
        public HashSet<string> Connected { get; } = [];

        public void Join(string device) => Connected.Add(device);

        public void ChangeDevice(string caller, string target)
        {
            if (Connected.Contains(caller) && Connected.Contains(target))
                Active = target;
        }

        public void Start(string caller)
        {
            if (!Connected.Contains(caller))
                return;
            if (Active is not null && Connected.Contains(Active))
            {
                if (!Item)
                    TimeMs = null;
                Item = true;
                return;
            }
            Active = caller;
            if (!Item)
                TimeMs = null;
            Item = true;
        }

        public void Report(string caller, int seconds)
        {
            if (Item && caller == Active)
                TimeMs = seconds * 1000;
        }

        public void Disconnect(string device)
        {
            Connected.Remove(device);
            if (Active == device)
                Active = null;
            if (Connected.Count == 0)
            {
                Active = null;
                Item = false;
                TimeMs = null;
            }
        }
    }

    // Part 6: random actors and actions, a reducer as oracle, I1 / I7 / I12 after every step.
    [Theory]
    [InlineData(Kind.Video, 1)]
    [InlineData(Kind.Video, 2)]
    [InlineData(Kind.Video, 3)]
    [InlineData(Kind.Video, 4)]
    [InlineData(Kind.Music, 1)]
    [InlineData(Kind.Music, 2)]
    [InlineData(Kind.Music, 3)]
    [InlineData(Kind.Music, 4)]
    public async Task RandomOrder_FixedSeed_FollowsTheSessionModel(Kind kind, int seed)
    {
        await RunRandomSequenceAsync(kind, seed);
    }

    [Theory]
    [InlineData(Kind.Video)]
    [InlineData(Kind.Music)]
    public async Task RandomOrder_FreshSeed_FollowsTheSessionModel(Kind kind)
    {
        int seed = Random.Shared.Next(1000, int.MaxValue);
        output.WriteLine($"fresh seed {seed} kind {kind}");
        await RunRandomSequenceAsync(kind, seed);
    }

    private async Task RunRandomSequenceAsync(Kind kind, int seed)
    {
        Random random = new(seed);
        Actor[] actors =
        [
            await ConnectAsync(kind, "a"),
            await ConnectAsync(kind, "b"),
            await ConnectAsync(kind, "c"),
        ];
        Model model = new();
        foreach (Actor actor in actors)
            model.Join(actor.DeviceId);
        List<string> trace = [];

        for (int step = 1; step <= RandomSteps; step++)
        {
            Actor actor = actors[random.Next(actors.Length)];
            int roll = random.Next(100);
            if (!actor.Connected)
            {
                trace.Add($"{step}: {actor.Name} joins");
                await ConnectAsync(kind, actor.Name);
                model.Join(actor.DeviceId);
            }
            else if (roll < 30)
            {
                Actor target = actors[random.Next(actors.Length)];
                trace.Add($"{step}: {actor.Name} changes device to {target.Name}");
                await ChangeDeviceAsync(actor, target);
                model.ChangeDevice(actor.DeviceId, target.DeviceId);
            }
            else if (roll < 55)
            {
                trace.Add($"{step}: {actor.Name} starts");
                await StartAsync(kind, actor);
                model.Start(actor.DeviceId);
            }
            else if (roll < 75)
            {
                int seconds = 10 + random.Next(500);
                trace.Add($"{step}: {actor.Name} reports time {seconds}s");
                await ReportTimeAsync(kind, actor, seconds);
                model.Report(actor.DeviceId, seconds);
            }
            else
            {
                trace.Add($"{step}: {actor.Name} disconnects");
                await DisconnectAsync(actor);
                model.Disconnect(actor.DeviceId);
            }

            await SettleAsync(() => Matches(kind, model), TimeSpan.FromSeconds(2));
            SessionView actual = View(kind);
            string story =
                $"seed {seed} kind {kind}\n{string.Join("\n", trace)}\nexpected active={Name(actors, model.Active)} item={model.Item} time={model.TimeMs}\nactual   active={Name(actors, actual.Active)} item={actual.HasItem} time={actual.TimeMs}";
            actual
                .Active.Should()
                .Be(model.Active, $"I1/I12: the active device follows the model\n{story}");
            actual.HasItem.Should().Be(model.Item, $"the item follows the model\n{story}");
            if (model.TimeMs is { } expectedTime)
                actual.TimeMs.Should().Be(expectedTime, $"I7: only the active clock moves\n{story}");
            if (kind == Kind.Music)
                MusicRegistryDevice()
                    .Should()
                    .Be(actual.Active, $"I1: state and registry name the same device\n{story}");
        }
    }

    private bool Matches(Kind kind, Model model)
    {
        SessionView actual = View(kind);
        return actual.Active == model.Active
            && actual.HasItem == model.Item
            && (model.TimeMs is null || actual.TimeMs == model.TimeMs);
    }

    private static string Name(Actor[] actors, string? deviceId) =>
        actors.FirstOrDefault(a => a.DeviceId == deviceId)?.Name ?? deviceId ?? "none";
}
