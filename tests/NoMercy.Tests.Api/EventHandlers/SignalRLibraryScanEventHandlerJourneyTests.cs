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
using NoMercy.Api.EventHandlers;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.Networking.Messaging;
using NoMercy.Networking.Messaging.EventHandlers;
using Xunit;

namespace NoMercy.Tests.Api.EventHandlers;

public class SignalRLibraryScanEventHandlerJourneyTests
{
    private sealed record Capture(string Method, string Hub, object? Payload);

    private static (
        InMemoryEventBus bus,
        List<Capture> calls,
        SignalRLibraryScanEventHandler handler
    ) BuildScanChain()
    {
        InMemoryEventBus bus = new();
        List<Capture> calls = [];
        Mock<IClientMessenger> messengerMock = new(MockBehavior.Loose);
        messengerMock
            .Setup(m => m.SendToAll(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, string, object>(
                (method, hub, payload) => calls.Add(new(method, hub, payload))
            )
            .Returns(Task.CompletedTask);
        SignalRLibraryScanEventHandler handler = new(
            NullLogger<SignalRLibraryScanEventHandler>.Instance,
            bus,
            messengerMock.Object
        );
        return (bus, calls, handler);
    }

    private static T? GetProp<T>(object? obj, string name)
    {
        if (obj is null)
            return default;
        object? val = obj.GetType().GetProperty(name)?.GetValue(obj);
        if (val is T typed)
            return typed;
        return default;
    }

    [Fact]
    public async Task LibraryScanStarted_PublishViaRealBus_CallsSendToAll_DashboardHub_LibraryScanStarted_WithCorrectPayload()
    {
        (InMemoryEventBus bus, List<Capture> calls, SignalRLibraryScanEventHandler handler) =
            BuildScanChain();
        using SignalRLibraryScanEventHandler _ = handler;

        Ulid libraryId = Ulid.NewUlid();
        await bus.PublishAsync(
            new LibraryScanStartedEvent { LibraryId = libraryId, LibraryName = "Movies" }
        );

        Capture call = calls.Should().ContainSingle().Which;
        call.Method.Should().Be("LibraryScanStarted");
        call.Hub.Should().Be("dashboardHub");
        GetProp<string>(call.Payload, "LibraryId").Should().Be(libraryId.ToString());
        GetProp<string>(call.Payload, "LibraryName").Should().Be("Movies");
    }

    [Fact]
    public async Task LibraryScanCompleted_PublishViaRealBus_CallsSendToAll_DashboardHub_LibraryScanCompleted_WithCorrectPayload()
    {
        (InMemoryEventBus bus, List<Capture> calls, SignalRLibraryScanEventHandler handler) =
            BuildScanChain();
        using SignalRLibraryScanEventHandler _ = handler;

        Ulid libraryId = Ulid.NewUlid();
        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = libraryId,
                LibraryName = "TV Shows",
                ItemsFound = 42,
                Duration = TimeSpan.FromSeconds(15),
            }
        );

        Capture call = calls.Should().ContainSingle().Which;
        call.Method.Should().Be("LibraryScanCompleted");
        call.Hub.Should().Be("dashboardHub");
        GetProp<string>(call.Payload, "LibraryId").Should().Be(libraryId.ToString());
        GetProp<string>(call.Payload, "LibraryName").Should().Be("TV Shows");
        GetProp<int>(call.Payload, "ItemsFound").Should().Be(42);
        GetProp<double>(call.Payload, "Duration").Should().BeApproximately(15.0, 0.001);
    }

    [Fact]
    public async Task MediaAdded_PublishViaRealBus_CallsSendToAll_DashboardHub_MediaAdded_WithCorrectPayload()
    {
        (InMemoryEventBus bus, List<Capture> calls, SignalRLibraryScanEventHandler handler) =
            BuildScanChain();
        using SignalRLibraryScanEventHandler _ = handler;

        Ulid libraryId = Ulid.NewUlid();
        await bus.PublishAsync(
            new MediaAddedEvent
            {
                MediaId = 999,
                MediaType = "movie",
                Title = "Inception",
                LibraryId = libraryId,
            }
        );

        Capture call = calls.Should().ContainSingle().Which;
        call.Method.Should().Be("MediaAdded");
        call.Hub.Should().Be("dashboardHub");
        GetProp<int>(call.Payload, "MediaId").Should().Be(999);
        GetProp<string>(call.Payload, "MediaType").Should().Be("movie");
        GetProp<string>(call.Payload, "Title").Should().Be("Inception");
        GetProp<string>(call.Payload, "LibraryId").Should().Be(libraryId.ToString());
    }

    [Fact]
    public async Task MediaRemoved_PublishViaRealBus_CallsSendToAll_DashboardHub_MediaRemoved_WithCorrectPayload()
    {
        (InMemoryEventBus bus, List<Capture> calls, SignalRLibraryScanEventHandler handler) =
            BuildScanChain();
        using SignalRLibraryScanEventHandler _ = handler;

        Ulid libraryId = Ulid.NewUlid();
        await bus.PublishAsync(
            new MediaRemovedEvent
            {
                MediaId = 7,
                MediaType = "tv",
                Title = "Breaking Bad",
                LibraryId = libraryId,
            }
        );

        Capture call = calls.Should().ContainSingle().Which;
        call.Method.Should().Be("MediaRemoved");
        call.Hub.Should().Be("dashboardHub");
        GetProp<int>(call.Payload, "MediaId").Should().Be(7);
        GetProp<string>(call.Payload, "MediaType").Should().Be("tv");
        GetProp<string>(call.Payload, "Title").Should().Be("Breaking Bad");
        GetProp<string>(call.Payload, "LibraryId").Should().Be(libraryId.ToString());
    }

    [Fact]
    public async Task AllFourEvents_EachCallSendToAll_ExactlyOnce_OnDashboardHub_ForTheirOwnMethod()
    {
        (InMemoryEventBus bus, List<Capture> calls, SignalRLibraryScanEventHandler handler) =
            BuildScanChain();
        using SignalRLibraryScanEventHandler _ = handler;

        Ulid id = Ulid.NewUlid();

        await bus.PublishAsync(new LibraryScanStartedEvent { LibraryId = id, LibraryName = "L1" });
        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = id,
                LibraryName = "L1",
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
                LibraryId = id,
            }
        );
        await bus.PublishAsync(
            new MediaRemovedEvent
            {
                MediaId = 2,
                MediaType = "tv",
                Title = "B",
                LibraryId = id,
            }
        );

        calls.Should().HaveCount(4);
        calls.Should().OnlyContain(c => c.Hub == "dashboardHub");
        calls
            .Select(c => c.Method)
            .Should()
            .ContainInOrder([
                "LibraryScanStarted",
                "LibraryScanCompleted",
                "MediaAdded",
                "MediaRemoved",
            ]);
    }

    [Fact]
    public async Task Dispose_StopsAllDelivery_ForAllFourEvents()
    {
        (InMemoryEventBus bus, List<Capture> calls, SignalRLibraryScanEventHandler handler) =
            BuildScanChain();

        Ulid id = Ulid.NewUlid();
        await bus.PublishAsync(new LibraryScanStartedEvent { LibraryId = id, LibraryName = "L" });

        calls.Should().HaveCount(1);

        handler.Dispose();

        await bus.PublishAsync(new LibraryScanStartedEvent { LibraryId = id, LibraryName = "L2" });
        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = id,
                LibraryName = "L",
                ItemsFound = 0,
                Duration = TimeSpan.Zero,
            }
        );

        calls.Should().HaveCount(1, "dispose removed all subscriptions");
    }
}

public class SignalRLibraryRefreshEventHandlerJourneyTests
{
    private sealed record Capture(string Method, string Hub, object? Payload);

    private static (
        InMemoryEventBus bus,
        List<Capture> calls,
        SignalRLibraryRefreshEventHandler handler
    ) BuildRefreshChain()
    {
        InMemoryEventBus bus = new();
        List<Capture> calls = [];
        Mock<IClientMessenger> messengerMock = new(MockBehavior.Loose);
        messengerMock
            .Setup(m => m.SendToAll(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, string, object>(
                (method, hub, payload) => calls.Add(new(method, hub, payload))
            )
            .Returns(Task.CompletedTask);
        SignalRLibraryRefreshEventHandler handler = new(bus, messengerMock.Object);
        return (bus, calls, handler);
    }

    [Fact]
    public async Task LibraryRefreshed_PublishViaRealBus_CallsSendToAll_VideoHub_RefreshLibrary_WithQueryKey()
    {
        (InMemoryEventBus bus, List<Capture> calls, SignalRLibraryRefreshEventHandler handler) =
            BuildRefreshChain();
        using SignalRLibraryRefreshEventHandler _ = handler;

        object?[] queryKey = ["movies", 1, null];
        await bus.PublishAsync(new LibraryRefreshedEvent { QueryKey = queryKey });

        Capture call = calls.Should().ContainSingle().Which;
        call.Method.Should().Be("RefreshLibrary");
        call.Hub.Should().Be("videoHub");
    }

    [Fact]
    public async Task LibraryRefreshed_DoesNotPublish_ToDashboardHub()
    {
        (InMemoryEventBus bus, List<Capture> calls, SignalRLibraryRefreshEventHandler handler) =
            BuildRefreshChain();
        using SignalRLibraryRefreshEventHandler _ = handler;

        await bus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["key"] });

        calls.Should().OnlyContain(c => c.Hub != "dashboardHub");
    }

    [Fact]
    public async Task Dispose_StopsDelivery_ForLibraryRefreshedEvent()
    {
        (InMemoryEventBus bus, List<Capture> calls, SignalRLibraryRefreshEventHandler handler) =
            BuildRefreshChain();

        await bus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["a"] });

        calls.Should().HaveCount(1);

        handler.Dispose();

        await bus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["b"] });

        calls.Should().HaveCount(1, "dispose removed the subscription");
    }
}

public class LibraryScanCompletedFanOutJourneyTests
{
    [Fact]
    public async Task LibraryScanCompletedEvent_FansOut_ToBothSignalRHandlerAndTestListener_FromSinglePublish()
    {
        InMemoryEventBus bus = new();
        List<string> signalRCalls = [];
        Mock<IClientMessenger> messengerMock = new(MockBehavior.Loose);
        messengerMock
            .Setup(m => m.SendToAll(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, string, object>((method, _, _) => signalRCalls.Add(method))
            .Returns(Task.CompletedTask);

        using SignalRLibraryScanEventHandler scanHandler = new(
            NullLogger<SignalRLibraryScanEventHandler>.Instance,
            bus,
            messengerMock.Object
        );

        List<LibraryScanCompletedEvent> receivedByTestListener = [];
        using IDisposable testSubscription = bus.Subscribe<LibraryScanCompletedEvent>(
            (evt, _) =>
            {
                receivedByTestListener.Add(evt);
                return Task.CompletedTask;
            }
        );

        Ulid libraryId = Ulid.NewUlid();
        LibraryScanCompletedEvent published = new()
        {
            LibraryId = libraryId,
            LibraryName = "Fan-Out Library",
            ItemsFound = 5,
            Duration = TimeSpan.FromSeconds(3),
        };

        await bus.PublishAsync(published);

        signalRCalls.Should().Contain("LibraryScanCompleted");
        receivedByTestListener.Should().ContainSingle();
        receivedByTestListener[0].LibraryId.Should().Be(libraryId);
    }
}
