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

using Moq;
using NoMercy.Notifications.Push;
using NoMercy.Notifications.Transports;
using Xunit;

namespace NoMercy.Tests.Notifications.Push;

public class PushDispatchQueueTests
{
    // Long enough that a runner busy with a whole coverage pass cannot hit it,
    // short enough that a loop which genuinely stopped still fails fast. Five
    // seconds was neither: CI timed out here on a queue that was working.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static PushDispatchRequest Request(string channel = "encode-finished") =>
        new(channel, new PushPayload("Done", "body", null), "token");

    /// <summary>
    /// The producer here is whatever published the event. If it ever waits on
    /// the relay, a server that cannot reach nomercy.tv pays the full HTTP
    /// timeout on every publish, and a request that publishes four events pays
    /// it four times over.
    /// </summary>
    [Fact]
    public async Task Enqueue_Returns_While_The_Relay_Is_Still_Stuck_On_The_First_Send()
    {
        TaskCompletionSource entered = new();
        TaskCompletionSource release = new();

        Mock<IPushDispatcher> dispatcher = new();
        dispatcher
            .Setup(d =>
                d.DispatchAsync(
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
                return release.Task;
            });

        PushDispatchQueue queue = new(dispatcher.Object, new NotificationDispatcher([]));
        using CancellationTokenSource cts = new();
        Task drain = queue.DrainAsync(cts.Token);

        queue.Enqueue(Request());
        await entered.Task.WaitAsync(Patience);

        queue.Enqueue(Request());
        queue.Enqueue(Request());

        Assert.False(release.Task.IsCompleted);
        dispatcher.Verify(
            d =>
                d.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<PushPayload>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        release.SetResult();
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Drain_Delivers_What_Was_Enqueued()
    {
        TaskCompletionSource delivered = new();

        Mock<IPushDispatcher> dispatcher = new();
        dispatcher
            .Setup(d =>
                d.DispatchAsync(
                    "encode-failed",
                    It.IsAny<PushPayload>(),
                    "token",
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(() => delivered.TrySetResult())
            .Returns(Task.CompletedTask);

        PushDispatchQueue queue = new(dispatcher.Object, new NotificationDispatcher([]));
        using CancellationTokenSource cts = new();
        Task drain = queue.DrainAsync(cts.Token);

        queue.Enqueue(Request("encode-failed"));

        await delivered.Task.WaitAsync(Patience);
        await cts.CancelAsync();

        dispatcher.Verify(
            d =>
                d.DispatchAsync(
                    "encode-failed",
                    It.IsAny<PushPayload>(),
                    "token",
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    /// <summary>
    /// An HttpClient timeout arrives as a TaskCanceledException, which is an
    /// OperationCanceledException. If that ends the loop, one slow relay call
    /// silently kills push for the lifetime of the process.
    /// </summary>
    [Fact]
    public async Task Drain_Survives_A_Transport_Timeout_And_Keeps_Delivering()
    {
        TaskCompletionSource second = new();
        int calls = 0;

        Mock<IPushDispatcher> dispatcher = new();
        dispatcher
            .Setup(d =>
                d.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<PushPayload>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
            {
                calls++;
                if (calls == 1)
                    return Task.FromException(new TaskCanceledException("relay timed out"));

                second.TrySetResult();
                return Task.CompletedTask;
            });

        PushDispatchQueue queue = new(dispatcher.Object, new NotificationDispatcher([]));
        using CancellationTokenSource cts = new();
        Task drain = queue.DrainAsync(cts.Token);

        queue.Enqueue(Request());
        queue.Enqueue(Request());

        await second.Task.WaitAsync(Patience);
        Assert.False(drain.IsCompleted);

        await cts.CancelAsync();
    }

    /// <summary>
    /// Any exception a dispatcher lets escape must cost one request, not the loop.
    /// </summary>
    [Fact]
    public async Task Drain_Survives_An_Unexpected_Exception_And_Keeps_Delivering()
    {
        TaskCompletionSource second = new();
        int calls = 0;

        Mock<IPushDispatcher> dispatcher = new();
        dispatcher
            .Setup(d =>
                d.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<PushPayload>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
            {
                calls++;
                if (calls == 1)
                    return Task.FromException(new InvalidOperationException("boom"));

                second.TrySetResult();
                return Task.CompletedTask;
            });

        PushDispatchQueue queue = new(dispatcher.Object, new NotificationDispatcher([]));
        using CancellationTokenSource cts = new();
        Task drain = queue.DrainAsync(cts.Token);

        queue.Enqueue(Request());
        queue.Enqueue(Request());

        await second.Task.WaitAsync(Patience);
        Assert.False(drain.IsCompleted);

        await cts.CancelAsync();
    }

    /// <summary>
    /// A request carrying a UserId is one person's notification, and the
    /// channel-wide dispatcher would seal it for every subscriber this server
    /// can see. It has to leave through the transports instead, which pick
    /// SignalR or a user-filtered push.
    /// </summary>
    [Fact]
    public async Task Drain_Routes_A_User_Request_Through_The_Transports_Not_The_Channel_Dispatcher()
    {
        Guid userId = Guid.NewGuid();
        TaskCompletionSource delivered = new();
        UserNotification? captured = null;

        Mock<INotificationTransport> transport = new();
        transport.SetupGet(t => t.Name).Returns("Push");
        transport
            .Setup(t =>
                t.CanReachAsync(It.IsAny<UserNotification>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(true);
        transport
            .Setup(t => t.DeliverAsync(It.IsAny<UserNotification>(), It.IsAny<CancellationToken>()))
            .Callback<UserNotification, CancellationToken>(
                (notification, _) =>
                {
                    captured = notification;
                    delivered.TrySetResult();
                }
            )
            .Returns(Task.CompletedTask);

        Mock<IPushDispatcher> channelDispatcher = new();

        PushDispatchQueue queue = new(
            channelDispatcher.Object,
            new NotificationDispatcher([transport.Object])
        );
        using CancellationTokenSource cts = new();
        Task drain = queue.DrainAsync(cts.Token);

        queue.Enqueue(
            new(
                "user-notification",
                new PushPayload("Done", "body", null),
                "token",
                UserId: userId,
                Hub: "musicHub"
            )
        );

        await delivered.Task.WaitAsync(Patience);
        await cts.CancelAsync();

        Assert.NotNull(captured);
        Assert.Equal(userId, captured!.UserId);
        Assert.Equal("musicHub", captured.Hub);
        channelDispatcher.Verify(
            d =>
                d.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<PushPayload>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }
}
