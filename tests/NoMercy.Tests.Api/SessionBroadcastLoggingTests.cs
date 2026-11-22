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
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Logging;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Unit")]
public class SessionBroadcastLoggingTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static (ClientMessenger Messenger, CapturingLogger<ClientMessenger> Log) NewMessenger(
        Exception? sendFailure = null
    )
    {
        Mock<ISingleClientProxy> proxy = new();
        if (sendFailure is not null)
            proxy
                .Setup(p =>
                    p.SendCoreAsync(
                        It.IsAny<string>(),
                        It.IsAny<object?[]>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ThrowsAsync(sendFailure);

        ConnectedClients clients = new();
        clients.Clients.TryAdd(
            "connection-1",
            new Client
            {
                Sub = UserId,
                Endpoint = "/videoHub",
                DeviceId = "device-1",
                Socket = proxy.Object,
            }
        );
        CapturingLogger<ClientMessenger> log = new();
        return (new(clients, log, new SessionLogPolicy()), log);
    }

    [Fact]
    public async Task SendTo_ActiveDeviceChange_LogsInformationWithKindAndConnectionCount()
    {
        (ClientMessenger messenger, CapturingLogger<ClientMessenger> log) = NewMessenger();

        await messenger.SendTo("ChangeDevice", "videoHub", UserId, new { id = 1 });

        log.Entries.Should()
            .Contain(entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains("ChangeDevice")
                && entry.Message.Contains("1 connection")
            );
    }

    [Fact]
    public async Task SendTo_StateEventRepeatedInsideTheInterval_LogsDebug()
    {
        (ClientMessenger messenger, CapturingLogger<ClientMessenger> log) = NewMessenger();

        await messenger.SendTo("VideoPlayerState", "videoHub", UserId, new { id = 1 });
        await messenger.SendTo("VideoPlayerState", "videoHub", UserId, new { id = 2 });

        log.Entries.Where(entry =>
                entry.Message.Contains("VideoPlayerState") && entry.Message.Contains("target")
            )
            .Select(entry => entry.Level)
            .Should()
            .Equal(LogLevel.Information, LogLevel.Debug);
    }

    [Fact]
    public async Task SendTo_ConnectionThrows_LogsWarningWithReason()
    {
        (ClientMessenger messenger, CapturingLogger<ClientMessenger> log) = NewMessenger(
            new InvalidOperationException("socket closed")
        );

        await messenger.SendTo("ChangeDevice", "videoHub", UserId, new { id = 1 });

        log.Entries.Should()
            .Contain(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains("device-1")
                && entry.Message.Contains("socket closed")
            );
    }
}
