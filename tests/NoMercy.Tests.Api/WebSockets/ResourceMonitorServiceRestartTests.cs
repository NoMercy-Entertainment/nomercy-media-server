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

using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.WebSockets;
using NoMercy.Monitoring;
using NoMercy.Networking.Messaging;
using Xunit;

namespace NoMercy.Tests.Api.WebSockets;

public class ResourceMonitorServiceRestartTests
{
    [Fact]
    public async Task Stop_WhenStartInterleaves_DoesNotCancelReplacementBroadcast()
    {
        ResourceMonitorService? service = null;
        bool restartOnStop = true;
        bool stopReturned = false;
        TaskCompletionSource updateAfterRestart = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Mock<IClientMessenger> clientMessenger = new();
        clientMessenger
            .Setup(x => x.SendToAll("ResourceUpdate", "dashboardHub", It.IsAny<object>()))
            .Returns(() =>
            {
                if (Volatile.Read(ref stopReturned))
                    updateAfterRestart.TrySetResult();
                return Task.CompletedTask;
            });
        Mock<ILogger<ResourceMonitorService>> logger = new();
        logger
            .Setup(x =>
                x.Log(
                    It.IsAny<LogLevel>(),
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                )
            )
            .Callback(
                new InvocationAction(invocation =>
                {
                    if (
                        restartOnStop
                        && invocation.Arguments[2].ToString()
                            == "Stopping resource monitoring broadcast"
                    )
                    {
                        restartOnStop = false;
                        service!.Start();
                    }
                })
            );

        using ResourceMonitor resourceMonitor = new(NullLogger<ResourceMonitor>.Instance);
        resourceMonitor.Stop();
        service = new(logger.Object, resourceMonitor, clientMessenger.Object);
        service.Start();

        service.Stop();
        Volatile.Write(ref stopReturned, true);

        FieldInfo sourceField = typeof(ResourceMonitorService).GetField(
            "_cancellationTokenSource",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        CancellationTokenSource source = (CancellationTokenSource)sourceField.GetValue(service)!;
        Assert.Equal(1, service.ActiveSubscribers);
        Assert.False(source.IsCancellationRequested);
        await updateAfterRestart.Task.WaitAsync(TimeSpan.FromSeconds(3));

        service.Stop();
    }
}
