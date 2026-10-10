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
using NoMercy.Authorization;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Playback;
using NoMercy.NmSystem.Auth;
using NoMercy.Notifications.Push;
using Xunit;

namespace NoMercy.Tests.Api.EventHandlers;

public sealed class PlaybackToolsNotificationTests
{
    [Fact]
    public async Task FailedDownload_NotifiesOwnerAndAdminsWithPlainEnglishReason()
    {
        InMemoryEventBus bus = new();
        Mock<IPushDispatchQueue> queue = new();
        Guid ownerId = Guid.NewGuid();
        Guid adminId = Guid.NewGuid();
        Mock<IUserCache> users = new();
        users
            .SetupGet(cache => cache.Users)
            .Returns([
                new User { Id = ownerId, Owner = true },
                new User { Id = adminId, Manage = true },
                new User { Id = Guid.NewGuid() },
            ]);
        using PushNotificationEventHandler handler = new(
            bus,
            new AuthTokenStore(),
            new NotificationSink(queue.Object),
            Mock.Of<IPlayableMediaProbe>(),
            userCache: users.Object
        );

        await bus.PublishAsync(
            new PlaybackToolsDownloadFailedEvent
            {
                ErrorMessage = "release feed unavailable",
                Attempt = 1,
                NextRetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            }
        );

        queue.Verify(
            q =>
                q.Enqueue(
                    It.Is<PushDispatchRequest>(r =>
                        r.UserId == ownerId
                        && r.Payload.Title == "Playback tools failed to download"
                        && r.Payload.Body.Contains("release feed unavailable")
                        && r.Payload.Body.Contains("Retrying")
                    )
                ),
            Times.Once
        );
        queue.Verify(
            q => q.Enqueue(It.Is<PushDispatchRequest>(r => r.UserId == adminId)),
            Times.Once
        );
        queue.Verify(q => q.Enqueue(It.IsAny<PushDispatchRequest>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RepeatedFailuresWithinThirtyMinutes_AreThrottledButRecoveryIsSentOnce()
    {
        InMemoryEventBus bus = new();
        Mock<IPushDispatchQueue> queue = new();
        Guid ownerId = Guid.NewGuid();
        Mock<IUserCache> users = new();
        users.SetupGet(cache => cache.Users).Returns([new User { Id = ownerId, Owner = true }]);
        using PushNotificationEventHandler handler = new(
            bus,
            new AuthTokenStore(),
            new NotificationSink(queue.Object),
            Mock.Of<IPlayableMediaProbe>(),
            userCache: users.Object
        );

        for (int attempt = 1; attempt <= 2; attempt++)
            await bus.PublishAsync(
                new PlaybackToolsDownloadFailedEvent
                {
                    ErrorMessage = "timeout",
                    Attempt = attempt,
                    NextRetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                }
            );
        await bus.PublishAsync(new PlaybackToolsReadyEvent { Attempt = 3 });
        await bus.PublishAsync(new PlaybackToolsReadyEvent { Attempt = 3 });

        queue.Verify(
            q =>
                q.Enqueue(
                    It.Is<PushDispatchRequest>(r =>
                        r.UserId == ownerId && r.Channel == "playback-tools-download-failed"
                    )
                ),
            Times.Once
        );
        queue.Verify(
            q =>
                q.Enqueue(
                    It.Is<PushDispatchRequest>(r =>
                        r.UserId == ownerId && r.Channel == "playback-tools-ready"
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task WaitingForAnOwner_GivesUpAfterTheLimitAndTheNextEventDeliversTheQueuedFailure()
    {
        InMemoryEventBus bus = new();
        Mock<IPushDispatchQueue> queue = new();
        Guid ownerId = Guid.NewGuid();
        List<User> cachedUsers = [];
        Mock<IUserCache> users = new();
        users.SetupGet(cache => cache.Users).Returns(() => cachedUsers);
        using PushNotificationEventHandler handler = new(
            bus,
            new AuthTokenStore(),
            new NotificationSink(queue.Object),
            Mock.Of<IPlayableMediaProbe>(),
            userCache: users.Object
        )
        {
            OwnerWaitInterval = TimeSpan.FromMilliseconds(20),
            OwnerWaitLimit = TimeSpan.FromMilliseconds(200),
        };

        await bus.PublishAsync(
            new PlaybackToolsDownloadFailedEvent
            {
                ErrorMessage = "timeout",
                Attempt = 1,
                NextRetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            }
        );
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        cachedUsers.Add(new User { Id = ownerId, Owner = true });
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        queue.Verify(q => q.Enqueue(It.IsAny<PushDispatchRequest>()), Times.Never);

        await bus.PublishAsync(new PlaybackToolsReadyEvent { Attempt = 2 });

        queue.Verify(
            q =>
                q.Enqueue(
                    It.Is<PushDispatchRequest>(r =>
                        r.UserId == ownerId && r.Channel == "playback-tools-download-failed"
                    )
                ),
            Times.Once
        );
        queue.Verify(
            q =>
                q.Enqueue(
                    It.Is<PushDispatchRequest>(r =>
                        r.UserId == ownerId && r.Channel == "playback-tools-ready"
                    )
                ),
            Times.Once
        );
    }
}
