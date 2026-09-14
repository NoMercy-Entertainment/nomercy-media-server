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

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.EventHandlers;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.Networking.Messaging.EventHandlers;
using NoMercy.NmSystem.Auth;
using NoMercy.Notifications.Push;
using NoMercy.Notifications.Transports;
using Xunit;

namespace NoMercy.Tests.Api.EventHandlers;

public class PushNotificationEventHandlerJourneyTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static (
        InMemoryEventBus bus,
        Mock<IPushDispatchQueue> queueMock,
        PushNotificationEventHandler handler
    ) BuildChain(string? accessToken = "server-access-token")
    {
        InMemoryEventBus bus = new();
        Mock<IPushDispatchQueue> queueMock = new();

        AuthTokenStore authTokenStore = new();
        authTokenStore.SetAccessToken(accessToken);

        NotificationSink sink = new(queueMock.Object);
        PushNotificationEventHandler handler = new(bus, authTokenStore, sink);
        return (bus, queueMock, handler);
    }

    private static EncodingCompletedEvent AnEncodeFinishing(
        string outputPath = "/output/movie/Idiocracy.m3u8"
    ) =>
        new()
        {
            JobId = 33,
            OutputPath = outputPath,
            Duration = TimeSpan.FromMinutes(90),
        };

    [Fact]
    public async Task EncodingCompleted_PublishViaRealBus_ReachesTheQueue_WithEncodeFinishedChannel()
    {
        (
            InMemoryEventBus bus,
            Mock<IPushDispatchQueue> queueMock,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await bus.PublishAsync(AnEncodeFinishing());

        queueMock.Verify(
            queue =>
                queue.Enqueue(
                    It.Is<PushDispatchRequest>(request =>
                        request.Channel == "encode-finished"
                        && request.Payload.Title == "Encoding finished"
                        && request.Payload.Body == "Idiocracy.m3u8 finished encoding"
                        && request.AccessToken == "server-access-token"
                    )
                ),
            Times.Once
        );
    }

    /// <summary>
    /// The event already carries the movie/episode artwork the encoder job
    /// resolved (FileMetadata.PosterPath/ImgPath); the handler's only job is to
    /// turn those bare TMDB paths into the same absolute, phone-reachable URL
    /// the web/KMP clients build themselves — not to look anything up itself.
    /// </summary>
    [Fact]
    public async Task EncodingCompleted_WithArtwork_CarriesImageAndIconOnThePayload()
    {
        (
            InMemoryEventBus bus,
            Mock<IPushDispatchQueue> queueMock,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await bus.PublishAsync(
            new EncodingCompletedEvent
            {
                JobId = 33,
                OutputPath = "/output/movie/Idiocracy.m3u8",
                Duration = TimeSpan.FromMinutes(90),
                PosterPath = "/poster123.jpg",
                BackdropPath = "/backdrop123.jpg",
            }
        );

        queueMock.Verify(
            queue =>
                queue.Enqueue(
                    It.Is<PushDispatchRequest>(request =>
                        request.Payload.Image != null
                        && request.Payload.Image.EndsWith("/tmdb-images/backdrop123.jpg?width=500")
                        && request.Payload.Icon != null
                        && request.Payload.Icon.EndsWith("/tmdb-images/poster123.jpg?width=200")
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task EncodingCompleted_WithoutArtwork_LeavesImageAndIconNull()
    {
        (
            InMemoryEventBus bus,
            Mock<IPushDispatchQueue> queueMock,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await bus.PublishAsync(AnEncodeFinishing());

        queueMock.Verify(
            queue =>
                queue.Enqueue(
                    It.Is<PushDispatchRequest>(request =>
                        request.Payload.Image == null && request.Payload.Icon == null
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task EncodingFailed_PublishViaRealBus_ReachesTheQueue_WithEncodeFailedChannel()
    {
        (
            InMemoryEventBus bus,
            Mock<IPushDispatchQueue> queueMock,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await bus.PublishAsync(
            new EncodingFailedEvent
            {
                JobId = 99,
                InputPath = "/input/corrupt.mkv",
                ErrorMessage = "FFmpeg exited with code 1",
            }
        );

        queueMock.Verify(
            queue =>
                queue.Enqueue(
                    It.Is<PushDispatchRequest>(request =>
                        request.Channel == "encode-failed"
                        && request.Payload.Title == "Encoding failed"
                        && request.Payload.Body == "FFmpeg exited with code 1"
                    )
                ),
            Times.Once
        );
    }

    /// <summary>
    /// LibraryRefreshedEvent is a cache-invalidation signal carrying a
    /// QueryKey. It is published from dozens of call sites and fires several
    /// times for one user action, including every continue-watching edit, so
    /// wiring it to push means every member's devices buzz on every scrub.
    /// </summary>
    [Fact]
    public async Task LibraryRefreshed_IsACacheSignal_AndPushesNothing()
    {
        (
            InMemoryEventBus bus,
            Mock<IPushDispatchQueue> queueMock,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await bus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["movies", 1] });
        await bus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["continue", 1] });

        queueMock.Verify(queue => queue.Enqueue(It.IsAny<PushDispatchRequest>()), Times.Never);
    }

    [Fact]
    public async Task NoAccessToken_SkipsPushEntirely()
    {
        (
            InMemoryEventBus bus,
            Mock<IPushDispatchQueue> queueMock,
            PushNotificationEventHandler handler
        ) = BuildChain(accessToken: null);
        using PushNotificationEventHandler _ = handler;

        await bus.PublishAsync(AnEncodeFinishing());

        queueMock.Verify(queue => queue.Enqueue(It.IsAny<PushDispatchRequest>()), Times.Never);
    }

    [Fact]
    public async Task Dispose_AfterSubscription_StopsPushDispatch()
    {
        (
            InMemoryEventBus bus,
            Mock<IPushDispatchQueue> queueMock,
            PushNotificationEventHandler handler
        ) = BuildChain();

        handler.Dispose();

        await bus.PublishAsync(AnEncodeFinishing());

        queueMock.Verify(queue => queue.Enqueue(It.IsAny<PushDispatchRequest>()), Times.Never);
    }

    /// <summary>
    /// InMemoryEventBus awaits its subscribers one after another. With the
    /// relay call inline, an unreachable nomercy.tv adds its full HTTP timeout
    /// to every publish, and a request that publishes four events waits four
    /// times over — on a self-hosted server that has to stay fully usable
    /// whether or not it can reach NoMercy.
    /// </summary>
    [Fact]
    public async Task PublishDoesNotWaitForTheRelay_EvenWhenItNeverAnswers()
    {
        TaskCompletionSource entered = new();
        TaskCompletionSource relayNeverAnswers = new();

        Mock<IPushDispatcher> dispatcherMock = new();
        dispatcherMock
            .Setup(dispatcher =>
                dispatcher.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<PushPayload>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
            {
                entered.TrySetResult();
                return relayNeverAnswers.Task;
            });

        InMemoryEventBus bus = new();
        PushDispatchQueue queue = new(dispatcherMock.Object, new NotificationDispatcher([]));
        AuthTokenStore authTokenStore = new();
        authTokenStore.SetAccessToken("server-access-token");

        using CancellationTokenSource cts = new();
        Task drain = queue.DrainAsync(cts.Token);

        using PushNotificationEventHandler _ = new(bus, authTokenStore, new(queue));

        await bus.PublishAsync(AnEncodeFinishing());
        await entered.Task.WaitAsync(Patience);

        Task laterPublishes = Task.WhenAll(
            bus.PublishAsync(AnEncodeFinishing("/output/movie/Gattaca.m3u8")),
            bus.PublishAsync(AnEncodeFinishing("/output/movie/Primer.m3u8")),
            bus.PublishAsync(AnEncodeFinishing("/output/movie/Coherence.m3u8"))
        );

        Assert.True(laterPublishes.IsCompletedSuccessfully);
        Assert.False(relayNeverAnswers.Task.IsCompleted);

        relayNeverAnswers.SetResult();
        await cts.CancelAsync();
    }

    [Fact]
    public async Task PushDispatcherFailure_DoesNotBreakTheExistingSignalRPath()
    {
        InMemoryEventBus bus = new();

        Mock<IPushDispatcher> dispatcherMock = new();
        dispatcherMock
            .Setup(dispatcher =>
                dispatcher.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<PushPayload>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("relay unreachable"));

        AuthTokenStore authTokenStore = new();
        authTokenStore.SetAccessToken("server-access-token");

        PushDispatchQueue queue = new(dispatcherMock.Object, new NotificationDispatcher([]));
        using CancellationTokenSource cts = new();
        Task drain = queue.DrainAsync(cts.Token);

        using PushNotificationEventHandler pushHandler = new(bus, authTokenStore, new(queue));

        Mock<NoMercy.Networking.Messaging.IClientMessenger> messengerMock = new(
            MockBehavior.Strict
        );
        messengerMock
            .Setup(messenger =>
                messenger.SendToAll(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>())
            )
            .Returns(Task.CompletedTask);

        using SignalREncodingEventHandler signalRHandler = new(
            NullLogger<SignalREncodingEventHandler>.Instance,
            bus,
            messengerMock.Object
        );

        await bus.PublishAsync(AnEncodeFinishing("/output/x.m3u8"));

        messengerMock.Verify(
            messenger =>
                messenger.SendToAll("EncodingCompleted", "dashboardHub", It.IsAny<object>()),
            Times.Once
        );

        await cts.CancelAsync();
    }

    private sealed class UserNotifiedChain : IDisposable
    {
        public required InMemoryEventBus Bus { get; init; }
        public required SignalRNotificationEventHandler SignalRHandler { get; init; }
        public required PushNotificationEventHandler PushHandler { get; init; }
        public required Mock<ISingleClientProxy> Socket { get; init; }
        public required Mock<IPushRelayClient> Relay { get; init; }
        public required TaskCompletionSource Settled { get; init; }
        public required CancellationTokenSource Drain { get; init; }

        public void Dispose()
        {
            SignalRHandler.Dispose();
            PushHandler.Dispose();
            Drain.Cancel();
            Drain.Dispose();
        }
    }

    /// <summary>
    /// The whole per-user path as it is wired in production: the real bus, the
    /// real queue and worker loop, the real dispatcher, and both real
    /// transports. Only the socket and the relay HTTP call are stood in for,
    /// because those are what the guarantee is stated about.
    /// </summary>
    private static UserNotifiedChain BuildUserNotifiedChain(
        Guid userId,
        bool connected,
        string hub = "videoHub"
    )
    {
        InMemoryEventBus bus = new();
        ConnectedClients connectedClients = new();
        TaskCompletionSource settled = new();

        Mock<ISingleClientProxy> socket = new();
        socket
            .Setup(s =>
                s.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(() => settled.TrySetResult())
            .Returns(Task.CompletedTask);

        if (connected)
        {
            connectedClients.Clients.TryAdd(
                "conn-1",
                new Client
                {
                    Sub = userId,
                    Socket = socket.Object,
                    Endpoint = "/" + hub,
                }
            );
        }

        ClientMessenger clientMessenger = new(
            connectedClients,
            NullLogger<ClientMessenger>.Instance
        );
        SignalRNotificationEventHandler signalRHandler = new(bus, clientMessenger);

        AuthTokenStore authTokenStore = new();
        authTokenStore.SetAccessToken("server-access-token");

        Mock<IPushKeyClient> keyClient = new();
        keyClient
            .Setup(client => client.GetKeysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new(1, "p256dh", "auth", "ref-" + userId, userId)]);

        Mock<IWebPushEnvelope> envelope = new();
        envelope
            .Setup(e => e.Seal(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns([9, 8, 7]);

        Mock<IPushRelayClient> relay = new();
        relay
            .Setup(r =>
                r.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<PushRelayEntry>>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(() => settled.TrySetResult())
            .Returns(Task.CompletedTask);

        INotificationTransport signalRTransport = new SignalRNotificationTransport(
            connectedClients
        );
        INotificationTransport pushTransport = new PushNotificationTransport(
            keyClient.Object,
            envelope.Object,
            relay.Object,
            authTokenStore
        );

        PushDispatchQueue queue = new(
            new Mock<IPushDispatcher>().Object,
            new NotificationDispatcher([signalRTransport, pushTransport])
        );
        CancellationTokenSource drain = new();
        _ = queue.DrainAsync(drain.Token);

        NotificationSink sink = new(queue);
        PushNotificationEventHandler pushHandler = new(bus, authTokenStore, sink);

        return new()
        {
            Bus = bus,
            SignalRHandler = signalRHandler,
            PushHandler = pushHandler,
            Socket = socket,
            Relay = relay,
            Settled = settled,
            Drain = drain,
        };
    }

    private static UserNotifiedEvent ANotificationFor(Guid userId, string hub = "videoHub") =>
        new()
        {
            Title = "Encoding finished",
            Message = "Idiocracy finished encoding",
            Type = "info",
            Hub = hub,
            Route = "/movie/1",
            UserId = userId,
        };

    [Fact]
    public async Task UserNotified_ConnectedUser_ProducesExactlyOneSignalRMessage_AndNoPush()
    {
        Guid userId = Guid.NewGuid();
        using UserNotifiedChain chain = BuildUserNotifiedChain(userId, connected: true);

        await chain.Bus.PublishAsync(ANotificationFor(userId));
        await chain.Settled.Task.WaitAsync(Patience);

        chain.Socket.Verify(
            s => s.SendCoreAsync("Notify", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        chain.Relay.Verify(
            r =>
                r.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<PushRelayEntry>>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    /// <summary>
    /// A user connected to a different hub is not reachable on the target one:
    /// suppressing their push on the strength of that connection and then
    /// sending to zero sockets is the silent drop this path must not have.
    /// </summary>
    [Fact]
    public async Task UserNotified_UserConnectedToAnotherHub_StillGetsThePush()
    {
        Guid userId = Guid.NewGuid();
        using UserNotifiedChain chain = BuildUserNotifiedChain(
            userId,
            connected: true,
            hub: "musicHub"
        );

        await chain.Bus.PublishAsync(ANotificationFor(userId, hub: "videoHub"));
        await chain.Settled.Task.WaitAsync(Patience);

        chain.Socket.Verify(
            s =>
                s.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        chain.Relay.Verify(
            r =>
                r.DispatchAsync(
                    "user-notification",
                    It.IsAny<IReadOnlyList<PushRelayEntry>>(),
                    "server-access-token",
                    "ref-" + userId,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task UserNotified_DisconnectedUser_ProducesExactlyOnePush_WithAudience_AndNoSignalR()
    {
        Guid userId = Guid.NewGuid();
        using UserNotifiedChain chain = BuildUserNotifiedChain(userId, connected: false);

        await chain.Bus.PublishAsync(ANotificationFor(userId));
        await chain.Settled.Task.WaitAsync(Patience);

        chain.Socket.Verify(
            s =>
                s.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        chain.Relay.Verify(
            r =>
                r.DispatchAsync(
                    "user-notification",
                    It.IsAny<IReadOnlyList<PushRelayEntry>>(),
                    "server-access-token",
                    "ref-" + userId,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    /// <summary>
    /// The per-user case runs through the same queue as the broadcast one, so
    /// the publisher never waits on nomercy.tv. With the relay call inline,
    /// POST /dashboard/notifications/send blocks for the full HTTP timeout of a
    /// server that cannot currently reach NoMercy.
    /// </summary>
    [Fact]
    public async Task UserNotified_PublishDoesNotWaitForTheRelay_EvenWhenItNeverAnswers()
    {
        Guid userId = Guid.NewGuid();
        TaskCompletionSource entered = new();
        TaskCompletionSource relayNeverAnswers = new();

        InMemoryEventBus bus = new();
        AuthTokenStore authTokenStore = new();
        authTokenStore.SetAccessToken("server-access-token");

        Mock<IPushKeyClient> keyClient = new();
        keyClient
            .Setup(client => client.GetKeysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new(1, "p256dh", "auth", "ref-" + userId, userId)]);

        Mock<IWebPushEnvelope> envelope = new();
        envelope
            .Setup(e => e.Seal(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns([9, 8, 7]);

        Mock<IPushRelayClient> relay = new();
        relay
            .Setup(r =>
                r.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<PushRelayEntry>>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
            {
                entered.TrySetResult();
                return relayNeverAnswers.Task;
            });

        INotificationTransport pushTransport = new PushNotificationTransport(
            keyClient.Object,
            envelope.Object,
            relay.Object,
            authTokenStore
        );

        PushDispatchQueue queue = new(
            new Mock<IPushDispatcher>().Object,
            new NotificationDispatcher([pushTransport])
        );
        using CancellationTokenSource cts = new();
        _ = queue.DrainAsync(cts.Token);

        using PushNotificationEventHandler handler = new(bus, authTokenStore, new(queue));

        await bus.PublishAsync(ANotificationFor(userId));
        await entered.Task.WaitAsync(Patience);

        Task laterPublishes = Task.WhenAll(
            bus.PublishAsync(ANotificationFor(userId)),
            bus.PublishAsync(ANotificationFor(userId)),
            bus.PublishAsync(ANotificationFor(userId))
        );

        Assert.True(laterPublishes.IsCompletedSuccessfully);
        Assert.False(relayNeverAnswers.Task.IsCompleted);

        relayNeverAnswers.SetResult();
        await cts.CancelAsync();
    }
}
