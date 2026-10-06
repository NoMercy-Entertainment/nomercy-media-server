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
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.NmSystem.Auth;
using NoMercy.Notifications.Push;
using NoMercy.Notifications.Transports;
using Xunit;

namespace NoMercy.Tests.Api.EventHandlers;

public class PushNotificationScanFinishedTests
{
    private static readonly Ulid LibraryId = Ulid.NewUlid();

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static (
        InMemoryEventBus bus,
        List<PushDispatchRequest> pushes,
        PushNotificationEventHandler handler
    ) BuildChain(TimeProvider? timeProvider = null)
    {
        InMemoryEventBus bus = new();
        List<PushDispatchRequest> pushes = [];
        Mock<IPushDispatchQueue> queueMock = new();
        queueMock
            .Setup(queue => queue.Enqueue(It.IsAny<PushDispatchRequest>()))
            .Callback<PushDispatchRequest>(pushes.Add);

        AuthTokenStore authTokenStore = new();
        authTokenStore.SetAccessToken("server-access-token");

        PushNotificationEventHandler handler = new(
            bus,
            authTokenStore,
            new NotificationSink(queueMock.Object),
            new Mock<IPlayableMediaProbe>().Object,
            timeProvider
        );
        return (bus, pushes, handler);
    }

    private static LibraryScanStartedEvent AScanStarting(Ulid? libraryId = null) =>
        new() { LibraryId = libraryId ?? LibraryId, LibraryName = "Movies" };

    private static LibraryScanCompletedEvent AScanEnding(int itemsFound = 7) =>
        new()
        {
            LibraryId = LibraryId,
            LibraryName = "Movies",
            ItemsFound = itemsFound,
            Duration = TimeSpan.FromSeconds(3),
        };

    private static LibraryImportsQueuedEvent Queued(int count) =>
        new()
        {
            LibraryId = LibraryId,
            LibraryName = "Movies",
            Count = count,
        };

    private static MediaImportFinishedEvent Finished(int added, int failed = 0) =>
        new()
        {
            LibraryId = LibraryId,
            Added = added,
            Failed = failed,
        };

    private static List<PushDispatchRequest> ScanPushes(List<PushDispatchRequest> pushes) =>
        [.. pushes.Where(push => push.Channel == "library-scan-complete")];

    private static async Task AScanThatQueued(InMemoryEventBus bus, int count)
    {
        await bus.PublishAsync(AScanStarting());
        await bus.PublishAsync(Queued(count));
        await bus.PublishAsync(AScanEnding());
    }

    [Fact]
    public async Task ScanEnds_WhileImportsArePending_SendsNoPush()
    {
        (
            InMemoryEventBus bus,
            List<PushDispatchRequest> pushes,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await AScanThatQueued(bus, 2);
        await bus.PublishAsync(Finished(added: 1));

        Assert.Empty(ScanPushes(pushes));
    }

    [Fact]
    public async Task LastImportFinishes_SendsOnePushWithTheTitlesAdded()
    {
        (
            InMemoryEventBus bus,
            List<PushDispatchRequest> pushes,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await AScanThatQueued(bus, 2);
        await bus.PublishAsync(Finished(added: 1));
        await bus.PublishAsync(Finished(added: 1));

        PushDispatchRequest push = Assert.Single(ScanPushes(pushes));
        Assert.Equal("Library scan finished", push.Payload.Title);
        Assert.Equal("Movies scanned, 2 title(s) added", push.Payload.Body);
        Assert.Equal("/libraries", push.Payload.Route);
    }

    [Fact]
    public async Task AnImportThatFailsForGood_CountsAsFinished()
    {
        (
            InMemoryEventBus bus,
            List<PushDispatchRequest> pushes,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await AScanThatQueued(bus, 2);
        await bus.PublishAsync(Finished(added: 1));
        await bus.PublishAsync(Finished(added: 0, failed: 1));

        PushDispatchRequest push = Assert.Single(ScanPushes(pushes));
        Assert.Equal("Movies scanned, 1 title(s) added, 1 failed", push.Payload.Body);
    }

    [Fact]
    public async Task ImportsFinishBeforeTheQueuedEventArrives_StillSendOnePush()
    {
        (
            InMemoryEventBus bus,
            List<PushDispatchRequest> pushes,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await bus.PublishAsync(AScanStarting());
        await bus.PublishAsync(Finished(added: 1));
        await bus.PublishAsync(Finished(added: 1));
        await bus.PublishAsync(Queued(2));
        Assert.Empty(ScanPushes(pushes));
        await bus.PublishAsync(AScanEnding());

        PushDispatchRequest push = Assert.Single(ScanPushes(pushes));
        Assert.Equal("Movies scanned, 2 title(s) added", push.Payload.Body);
    }

    [Fact]
    public async Task ScanThatQueuedNothing_PushesWhenItEnds_AsTheOldCodeDidForNothingNew()
    {
        (
            InMemoryEventBus bus,
            List<PushDispatchRequest> pushes,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await AScanThatQueued(bus, 0);

        PushDispatchRequest push = Assert.Single(ScanPushes(pushes));
        Assert.Equal("Movies scanned, 0 title(s) added", push.Payload.Body);
    }

    [Fact]
    public async Task TwoScansOfOneLibraryBeforeImportsFinish_SendOnePushAfterAllFinish()
    {
        (
            InMemoryEventBus bus,
            List<PushDispatchRequest> pushes,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await AScanThatQueued(bus, 1);
        await AScanThatQueued(bus, 1);
        await bus.PublishAsync(Finished(added: 1));
        Assert.Empty(ScanPushes(pushes));
        await bus.PublishAsync(Finished(added: 1));

        PushDispatchRequest push = Assert.Single(ScanPushes(pushes));
        Assert.Equal("Movies scanned, 2 title(s) added", push.Payload.Body);
    }

    [Fact]
    public async Task SecondScanWhoseJobsWereDroppedAsDuplicates_DoesNotWaitForThem()
    {
        (
            InMemoryEventBus bus,
            List<PushDispatchRequest> pushes,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await AScanThatQueued(bus, 1);
        await AScanThatQueued(bus, 0);
        Assert.Empty(ScanPushes(pushes));
        await bus.PublishAsync(Finished(added: 1));

        PushDispatchRequest push = Assert.Single(ScanPushes(pushes));
        Assert.Equal("Movies scanned, 1 title(s) added", push.Payload.Body);
    }

    [Fact]
    public async Task ImportFromBeforeTheUpgradeFinishingWithNoScanOpen_DoesNotPushOrGoNegative()
    {
        (
            InMemoryEventBus bus,
            List<PushDispatchRequest> pushes,
            PushNotificationEventHandler handler
        ) = BuildChain();
        using PushNotificationEventHandler _ = handler;

        await bus.PublishAsync(Finished(added: 1));
        Assert.Empty(ScanPushes(pushes));
        Assert.Equal(0, handler.PendingScanCount);

        await AScanThatQueued(bus, 1);
        Assert.Empty(ScanPushes(pushes));
        await bus.PublishAsync(Finished(added: 1));

        PushDispatchRequest push = Assert.Single(ScanPushes(pushes));
        Assert.Equal("Movies scanned, 1 title(s) added", push.Payload.Body);
    }

    [Fact]
    public async Task ScanThatNeverFinishes_IsDroppedAfterADay_AndItsLateImportsPushNothing()
    {
        AdjustableTimeProvider time = new();
        (
            InMemoryEventBus bus,
            List<PushDispatchRequest> pushes,
            PushNotificationEventHandler handler
        ) = BuildChain(time);
        using PushNotificationEventHandler _ = handler;

        await AScanThatQueued(bus, 1);
        Assert.Equal(1, handler.PendingScanCount);

        time.Advance(TimeSpan.FromHours(25));
        await bus.PublishAsync(AScanStarting(Ulid.NewUlid()));

        Assert.Equal(1, handler.PendingScanCount);
        await bus.PublishAsync(Finished(added: 1));
        Assert.Empty(ScanPushes(pushes));
    }
}
