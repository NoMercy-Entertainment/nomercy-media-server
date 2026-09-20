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

using FluentAssertions;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.Events.Playback;
using NoMercy.Events.Plugins;
using Xunit;

namespace NoMercy.Tests.Events;

public class LoggingEventBusDecoratorTests
{
    private sealed class TestEvent : EventBase
    {
        public override string Source => "TestSource";
        public string Data { get; init; } = string.Empty;
    }

    /// <summary>
    /// A failure event says what went wrong, not only that something did.
    /// <para>
    /// A plugin that would not load logged one line carrying an event name, an
    /// id and a timestamp. The reason was written to a logger nothing in the
    /// container was reading, so the owner saw a plugin missing from the list
    /// and had nowhere at all to find out why. That is the shape of the report
    /// this whole platform exists to answer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_event_that_explains_itself_has_its_reason_logged()
    {
        InMemoryEventBus inner = new();
        List<string> logMessages = [];
        LoggingEventBusDecorator decorator = new(inner, message => logMessages.Add(message));

        await decorator.PublishAsync(
            new PluginErrorOccurredEvent
            {
                PluginId = "1SBQT26FHF98EBRPYVRGD92CZF",
                PluginName = "Torrent Downloader",
                ErrorMessage = "Could not load type 'NoMercy.Plugin.TorrentDownloader.Plugin'.",
                ExceptionType = "ReflectionTypeLoadException",
            }
        );

        logMessages.Should().ContainSingle();
        logMessages[0].Should().Contain("Torrent Downloader");
        logMessages[0].Should().Contain("Could not load type");
        logMessages[0].Should().Contain("ReflectionTypeLoadException");
    }

    [Fact]
    public async Task An_event_with_nothing_to_explain_logs_the_line_it_always_did()
    {
        InMemoryEventBus inner = new();
        List<string> logMessages = [];
        LoggingEventBusDecorator decorator = new(inner, message => logMessages.Add(message));

        await decorator.PublishAsync(new TestEvent { Data = "hello" });

        logMessages[0].Should().Contain("TestEvent");
        logMessages[0].Should().NotContain(" | Why=");
    }

    [Fact]
    public async Task PublishAsync_LogsEventTypeName()
    {
        InMemoryEventBus inner = new();
        List<string> logMessages = [];
        LoggingEventBusDecorator decorator = new(inner, msg => logMessages.Add(msg));

        await decorator.PublishAsync(new TestEvent { Data = "hello" });

        logMessages.Should().ContainSingle();
        logMessages[0].Should().Contain("TestEvent");
    }

    [Fact]
    public async Task PublishAsync_LogsEventSource()
    {
        InMemoryEventBus inner = new();
        List<string> logMessages = [];
        LoggingEventBusDecorator decorator = new(inner, msg => logMessages.Add(msg));

        await decorator.PublishAsync(new TestEvent { Data = "hello" });

        logMessages[0].Should().Contain("Source=TestSource");
    }

    [Fact]
    public async Task PublishAsync_LogsEventId()
    {
        InMemoryEventBus inner = new();
        List<string> logMessages = [];
        LoggingEventBusDecorator decorator = new(inner, msg => logMessages.Add(msg));

        TestEvent evt = new() { Data = "hello" };
        await decorator.PublishAsync(evt);

        logMessages[0].Should().Contain($"EventId={evt.EventId}");
    }

    [Fact]
    public async Task PublishAsync_LogsTimestamp()
    {
        InMemoryEventBus inner = new();
        List<string> logMessages = [];
        LoggingEventBusDecorator decorator = new(inner, msg => logMessages.Add(msg));

        await decorator.PublishAsync(new TestEvent { Data = "hello" });

        logMessages[0].Should().Contain("Timestamp=");
    }

    [Fact]
    public async Task PublishAsync_DelegatesSubscribersToInnerBus()
    {
        InMemoryEventBus inner = new();
        List<string> logMessages = [];
        LoggingEventBusDecorator decorator = new(inner, msg => logMessages.Add(msg));

        List<TestEvent> received = [];
        decorator.Subscribe<TestEvent>(
            (evt, _) =>
            {
                received.Add(evt);
                return Task.CompletedTask;
            }
        );

        TestEvent testEvent = new() { Data = "test-data" };
        await decorator.PublishAsync(testEvent);

        received.Should().ContainSingle().Which.Data.Should().Be("test-data");
        logMessages.Should().ContainSingle();
    }

    [Fact]
    public async Task PublishAsync_LogsEachEventSeparately()
    {
        InMemoryEventBus inner = new();
        List<string> logMessages = [];
        LoggingEventBusDecorator decorator = new(inner, msg => logMessages.Add(msg));

        await decorator.PublishAsync(new TestEvent { Data = "first" });
        await decorator.PublishAsync(new TestEvent { Data = "second" });
        await decorator.PublishAsync(new TestEvent { Data = "third" });

        logMessages.Should().HaveCount(3);
    }

    [Fact]
    public async Task PublishAsync_LogsDifferentEventTypes()
    {
        InMemoryEventBus inner = new();
        List<string> logMessages = [];
        LoggingEventBusDecorator decorator = new(inner, msg => logMessages.Add(msg));

        await decorator.PublishAsync(
            new PlaybackStartedEvent
            {
                UserId = Guid.NewGuid(),
                MediaId = 1,
                MediaType = "movie",
            }
        );

        await decorator.PublishAsync(
            new EncodingStartedEvent
            {
                JobId = 1,
                InputPath = "/a.mkv",
                OutputPath = "/out/",
                ProfileName = "x264",
            }
        );

        await decorator.PublishAsync(
            new LibraryScanStartedEvent { LibraryId = Ulid.NewUlid(), LibraryName = "Movies" }
        );

        logMessages.Should().HaveCount(3);
        logMessages[0].Should().Contain("PlaybackStartedEvent").And.Contain("Source=Playback");
        logMessages[1].Should().Contain("EncodingStartedEvent").And.Contain("Source=Encoder");
        logMessages[2]
            .Should()
            .Contain("LibraryScanStartedEvent")
            .And.Contain("Source=LibraryScanner");
    }

    [Fact]
    public async Task Subscribe_ReturnsDisposable_UnsubscribesOnDispose()
    {
        InMemoryEventBus inner = new();
        LoggingEventBusDecorator decorator = new(inner, _ => { });

        List<TestEvent> received = [];
        IDisposable subscription = decorator.Subscribe<TestEvent>(
            (evt, _) =>
            {
                received.Add(evt);
                return Task.CompletedTask;
            }
        );

        await decorator.PublishAsync(new TestEvent { Data = "before" });
        received.Should().ContainSingle();

        subscription.Dispose();

        await decorator.PublishAsync(new TestEvent { Data = "after" });
        received.Should().ContainSingle("handler should not be called after dispose");
    }

    [Fact]
    public async Task Subscribe_WithEventHandler_DelegatesToInner()
    {
        InMemoryEventBus inner = new();
        LoggingEventBusDecorator decorator = new(inner, _ => { });

        TestHandler handler = new();
        decorator.Subscribe(handler);

        await decorator.PublishAsync(new TestEvent { Data = "handler-test" });

        handler.Received.Should().ContainSingle().Which.Data.Should().Be("handler-test");
    }

    [Fact]
    public void Constructor_NullInner_Throws()
    {
        Action act = () => new LoggingEventBusDecorator(null!, _ => { });
        act.Should().Throw<ArgumentNullException>().WithParameterName("inner");
    }

    [Fact]
    public void Constructor_NullLog_Throws()
    {
        InMemoryEventBus inner = new();
        Action act = () => new LoggingEventBusDecorator(inner, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("log");
    }

    [Fact]
    public async Task PublishAsync_LogsBeforeHandlersRun()
    {
        InMemoryEventBus inner = new();
        List<string> order = [];
        LoggingEventBusDecorator decorator = new(inner, _ => order.Add("logged"));

        decorator.Subscribe<TestEvent>(
            (_, _) =>
            {
                order.Add("handled");
                return Task.CompletedTask;
            }
        );

        await decorator.PublishAsync(new TestEvent());

        order.Should().Equal(["logged", "handled"]);
    }

    [Fact]
    public async Task PublishAsync_PropagatesCancellation()
    {
        InMemoryEventBus inner = new();
        LoggingEventBusDecorator decorator = new(inner, _ => { });
        CancellationTokenSource cts = new();

        decorator.Subscribe<TestEvent>(
            (_, _) =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            }
        );

        decorator.Subscribe<TestEvent>((_, _) => Task.CompletedTask);

        Func<Task> act = () => decorator.PublishAsync(new TestEvent(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PublishAsync_AllDomainEvents_AreLogged()
    {
        InMemoryEventBus inner = new();
        List<string> logMessages = [];
        LoggingEventBusDecorator decorator = new(inner, msg => logMessages.Add(msg));

        Guid userId = Guid.NewGuid();
        Ulid libraryId = Ulid.NewUlid();

        await decorator.PublishAsync(
            new PlaybackStartedEvent
            {
                UserId = userId,
                MediaId = 1,
                MediaType = "movie",
            }
        );
        await decorator.PublishAsync(
            new PlaybackProgressUpdatedEvent
            {
                UserId = userId,
                MediaId = 1,
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero,
            }
        );
        await decorator.PublishAsync(
            new PlaybackCompletedEvent
            {
                UserId = userId,
                MediaId = 1,
                MediaType = "movie",
            }
        );
        await decorator.PublishAsync(
            new EncodingStartedEvent
            {
                JobId = 1,
                InputPath = "/a",
                OutputPath = "/b",
                ProfileName = "x264",
            }
        );
        await decorator.PublishAsync(
            new EncodingProgressUpdatedEvent { JobId = 1, Percentage = 50 }
        );
        await decorator.PublishAsync(
            new EncodingCompletedEvent
            {
                JobId = 1,
                OutputPath = "/b",
                Duration = TimeSpan.Zero,
            }
        );
        await decorator.PublishAsync(
            new EncodingFailedEvent
            {
                JobId = 1,
                InputPath = "/a",
                ErrorMessage = "err",
            }
        );
        await decorator.PublishAsync(
            new LibraryScanStartedEvent { LibraryId = libraryId, LibraryName = "Movies" }
        );
        await decorator.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = libraryId,
                LibraryName = "Movies",
                ItemsFound = 0,
                Duration = TimeSpan.Zero,
            }
        );
        await decorator.PublishAsync(
            new MediaAddedEvent
            {
                MediaId = 1,
                MediaType = "movie",
                Title = "T",
                LibraryId = libraryId,
            }
        );
        await decorator.PublishAsync(
            new MediaRemovedEvent
            {
                MediaId = 1,
                MediaType = "movie",
                Title = "T",
                LibraryId = libraryId,
            }
        );

        logMessages.Should().HaveCount(11);
        logMessages.Should().OnlyContain(m => m.StartsWith("[Event]"));
        logMessages.Should().OnlyContain(m => m.Contains("EventId="));
        logMessages.Should().OnlyContain(m => m.Contains("Source="));
    }

    private sealed class TestHandler : IEventHandler<TestEvent>
    {
        public List<TestEvent> Received { get; } = [];

        public Task HandleAsync(TestEvent @event, CancellationToken ct = default)
        {
            Received.Add(@event);
            return Task.CompletedTask;
        }
    }
}
