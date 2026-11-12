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
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// Covers the live-incident fix to <see cref="ClientMessenger"/>: broadcasts used to await
/// each target connection sequentially, so one slow or half-dead connection (a backgrounded
/// phone, a zombie socket) delayed — and, depending on dictionary enumeration order, could
/// starve — delivery to every other connection in the same broadcast. Dispatch must now be
/// concurrent per connection, and a failing/slow connection must never prevent (or measurably
/// delay) delivery to a healthy one.
/// </summary>
[Trait("Category", "Unit")]
public class ClientMessengerTests
{
    private static Client MakeClient(
        Guid userId,
        string endpoint,
        Mock<ISingleClientProxy> proxy,
        string deviceId = "device-a"
    )
    {
        return new()
        {
            Id = Ulid.NewUlid(),
            Sub = userId,
            DeviceId = deviceId,
            Endpoint = endpoint,
            Socket = proxy.Object,
        };
    }

    [Fact]
    public async Task SendToModerators_ReachesOnlyModeratorConnectionsOnThatHub()
    {
        ConnectedClients connectedClients = new();
        Mock<ISingleClientProxy> moderatorProxy = new();
        Mock<ISingleClientProxy> guestProxy = new();
        Mock<ISingleClientProxy> otherHubProxy = new();

        Client moderator = MakeClient(Guid.NewGuid(), "/dashboardHub", moderatorProxy);
        moderator.IsModerator = true;
        Client otherHub = MakeClient(Guid.NewGuid(), "/musicHub", otherHubProxy);
        otherHub.IsModerator = true;
        connectedClients.Clients["mod"] = moderator;
        connectedClients.Clients["guest"] = MakeClient(Guid.NewGuid(), "/dashboardHub", guestProxy);
        connectedClients.Clients["other"] = otherHub;

        ClientMessenger messenger = new(connectedClients, NullLogger<ClientMessenger>.Instance);

        await messenger.SendToModerators("NewLog", "dashboardHub", "entry");

        moderatorProxy.Verify(
            p => p.SendCoreAsync("NewLog", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        guestProxy.Verify(
            p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        otherHubProxy.Verify(
            p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task SendTo_DispatchesConcurrently_SlowConnectionDoesNotDelayFastOne()
    {
        ConnectedClients connectedClients = new();
        Guid userId = Guid.NewGuid();

        Mock<ISingleClientProxy> slowProxy = new();
        slowProxy
            .Setup(p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(async () => await Task.Delay(500));

        Mock<ISingleClientProxy> fastProxy = new();
        TaskCompletionSource fastProxyCalled = new();
        fastProxy
            .Setup(p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(() => fastProxyCalled.TrySetResult())
            .Returns(Task.CompletedTask);

        connectedClients.Clients["slow-connection"] = MakeClient(
            userId,
            "/musicHub",
            slowProxy,
            "device-slow"
        );
        connectedClients.Clients["fast-connection"] = MakeClient(
            userId,
            "/musicHub",
            fastProxy,
            "device-fast"
        );

        ClientMessenger messenger = new(connectedClients, NullLogger<ClientMessenger>.Instance);

        Task sendTask = messenger.SendTo("MusicPlayerState", "musicHub", userId, new { });

        Task completed = await Task.WhenAny(
            fastProxyCalled.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250))
        );

        // The fast connection's send must be observed well inside the slow
        // connection's 500ms delay, proving the two dispatches run concurrently
        // rather than the fast one queuing behind the slow one.
        completed.Should().BeSameAs(fastProxyCalled.Task);

        await sendTask;
    }

    [Fact]
    public async Task SendTo_OneConnectionThrows_OtherConnectionStillReceivesMessage()
    {
        ConnectedClients connectedClients = new();
        Guid userId = Guid.NewGuid();

        Mock<ISingleClientProxy> throwingProxy = new();
        throwingProxy
            .Setup(p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("connection is gone"));

        Mock<ISingleClientProxy> healthyProxy = new();
        healthyProxy
            .Setup(p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);

        connectedClients.Clients["dead-connection"] = MakeClient(
            userId,
            "/musicHub",
            throwingProxy,
            "device-dead"
        );
        connectedClients.Clients["healthy-connection"] = MakeClient(
            userId,
            "/musicHub",
            healthyProxy,
            "device-healthy"
        );

        ClientMessenger messenger = new(connectedClients, NullLogger<ClientMessenger>.Instance);

        Func<Task> act = () => messenger.SendTo("MusicPlayerState", "musicHub", userId, new { });

        await act.Should().NotThrowAsync();

        healthyProxy.Verify(
            p =>
                p.SendCoreAsync(
                    "MusicPlayerState",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task SendToAll_OneConnectionThrows_OtherConnectionStillReceivesMessage()
    {
        ConnectedClients connectedClients = new();

        Mock<ISingleClientProxy> throwingProxy = new();
        throwingProxy
            .Setup(p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("connection is gone"));

        Mock<ISingleClientProxy> healthyProxy = new();
        healthyProxy
            .Setup(p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);

        connectedClients.Clients["dead-connection"] = MakeClient(
            Guid.NewGuid(),
            "/musicHub",
            throwingProxy,
            "device-dead"
        );
        connectedClients.Clients["healthy-connection"] = MakeClient(
            Guid.NewGuid(),
            "/musicHub",
            healthyProxy,
            "device-healthy"
        );

        ClientMessenger messenger = new(connectedClients, NullLogger<ClientMessenger>.Instance);

        Func<Task> act = () => messenger.SendToAll("ConnectedDevicesState", "musicHub", new { });

        await act.Should().NotThrowAsync();

        healthyProxy.Verify(
            p =>
                p.SendCoreAsync(
                    "ConnectedDevicesState",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task SendTo_OnlyTargetsMatchingUserAndEndpoint()
    {
        ConnectedClients connectedClients = new();
        Guid targetUser = Guid.NewGuid();
        Guid otherUser = Guid.NewGuid();

        Mock<ISingleClientProxy> targetProxy = new();
        targetProxy
            .Setup(p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);

        Mock<ISingleClientProxy> otherUserProxy = new();
        Mock<ISingleClientProxy> otherEndpointProxy = new();

        connectedClients.Clients["target"] = MakeClient(targetUser, "/musicHub", targetProxy);
        connectedClients.Clients["other-user"] = MakeClient(otherUser, "/musicHub", otherUserProxy);
        connectedClients.Clients["other-endpoint"] = MakeClient(
            targetUser,
            "/videoHub",
            otherEndpointProxy
        );

        ClientMessenger messenger = new(connectedClients, NullLogger<ClientMessenger>.Instance);

        await messenger.SendTo("MusicPlayerState", "musicHub", targetUser, new { });

        targetProxy.Verify(
            p =>
                p.SendCoreAsync(
                    "MusicPlayerState",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        otherUserProxy.Verify(
            p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        otherEndpointProxy.Verify(
            p =>
                p.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }
}
