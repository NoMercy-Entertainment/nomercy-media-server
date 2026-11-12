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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.WebSockets;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.Events.Playback;
using NoMercy.Monitoring;
using NoMercy.Networking.Messaging;
using NoMercy.Networking.Messaging.EventHandlers;
using Xunit;

namespace NoMercy.Tests.Api.EventHandlers;

public class DashboardHubAudienceTests
{
    private static void VerifyModeratorsOnly(
        Mock<IClientMessenger> messenger,
        string method,
        int times = 1
    )
    {
        messenger.Verify(
            m => m.SendToAll(method, It.IsAny<string>(), It.IsAny<object?>()),
            Times.Never
        );
        messenger.Verify(
            m => m.SendToModerators(method, "dashboardHub", It.IsAny<object?>()),
            Times.Exactly(times)
        );
    }

    [Fact]
    public async Task PlaybackEvents_AreSentToModeratorsOnly()
    {
        InMemoryEventBus bus = new();
        Mock<IClientMessenger> messenger = new();
        using SignalRPlaybackEventHandler handler = new(
            NullLogger<SignalRPlaybackEventHandler>.Instance,
            bus,
            messenger.Object
        );
        Guid userId = Guid.Parse("00000000-0000-4000-8000-000000000001");

        await bus.PublishAsync(
            new PlaybackStartedEvent
            {
                UserId = userId,
                MediaId = 1,
                MediaType = "movie",
            }
        );
        await bus.PublishAsync(
            new PlaybackProgressUpdatedEvent
            {
                UserId = userId,
                MediaId = 1,
                Position = TimeSpan.FromSeconds(5),
                Duration = TimeSpan.FromSeconds(50),
            }
        );
        await bus.PublishAsync(
            new PlaybackCompletedEvent
            {
                UserId = userId,
                MediaId = 1,
                MediaType = "movie",
            }
        );

        VerifyModeratorsOnly(messenger, "PlaybackStarted");
        VerifyModeratorsOnly(messenger, "PlaybackProgress");
        VerifyModeratorsOnly(messenger, "PlaybackCompleted");
    }

    [Fact]
    public async Task LibraryScanEvents_AreSentToModeratorsOnly()
    {
        InMemoryEventBus bus = new();
        Mock<IClientMessenger> messenger = new();
        using SignalRLibraryScanEventHandler handler = new(
            NullLogger<SignalRLibraryScanEventHandler>.Instance,
            bus,
            messenger.Object
        );
        Ulid libraryId = Ulid.NewUlid();

        await bus.PublishAsync(
            new LibraryScanStartedEvent { LibraryId = libraryId, LibraryName = "Movies" }
        );
        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = libraryId,
                LibraryName = "Movies",
                ItemsFound = 1,
                Duration = TimeSpan.Zero,
            }
        );
        await bus.PublishAsync(
            new MediaAddedEvent
            {
                MediaId = 1,
                MediaType = "movie",
                Title = "A",
                LibraryId = libraryId,
            }
        );
        await bus.PublishAsync(
            new MediaRemovedEvent
            {
                MediaId = 2,
                MediaType = "tv",
                Title = "B",
                LibraryId = libraryId,
            }
        );

        VerifyModeratorsOnly(messenger, "LibraryScanStarted");
        VerifyModeratorsOnly(messenger, "LibraryScanCompleted");
        VerifyModeratorsOnly(messenger, "MediaAdded");
        VerifyModeratorsOnly(messenger, "MediaRemoved");
    }

    [Fact]
    public async Task ResourceUpdate_IsSentToModeratorsOnly()
    {
        TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IClientMessenger> messenger = new();
        messenger
            .Setup(m => m.SendToModerators("ResourceUpdate", "dashboardHub", It.IsAny<object?>()))
            .Returns(() =>
            {
                sent.TrySetResult();
                return Task.CompletedTask;
            });
        using ResourceMonitor monitor = new(NullLogger<ResourceMonitor>.Instance);
        monitor.Stop();
        ResourceMonitorService service = new(
            NullLogger<ResourceMonitorService>.Instance,
            monitor,
            messenger.Object
        );

        service.Start();
        try
        {
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            service.Stop();
        }

        messenger.Verify(
            m => m.SendToAll("ResourceUpdate", It.IsAny<string>(), It.IsAny<object?>()),
            Times.Never
        );
    }
}
