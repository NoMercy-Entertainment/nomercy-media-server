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
using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Events.Audit;
using Xunit;

namespace NoMercy.Tests.Events;

public class LogCapture
{
    public List<string> Messages { get; } = [];
}

public class LoggingCapture : ILogger<InMemoryEventBus>
{
    private readonly LogCapture _capture;

    public LoggingCapture(LogCapture capture)
    {
        _capture = capture;
    }

    void ILogger.Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        _capture.Messages.Add($"{logLevel}:{formatter(state, exception)}");
    }

    bool ILogger.IsEnabled(LogLevel logLevel) => true;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
}

public class ComprehensiveCoverageTests
{
    private sealed class TestEvent : EventBase
    {
        public override string Source => "Test";
        public string Data { get; init; } = string.Empty;
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

    private sealed class SerializationThrowingEvent : EventBase
    {
        public override string Source => "ThrowingTest";

        [System.Text.Json.Serialization.JsonConverter(typeof(ThrowingJsonConverter))]
        public string ThrowingProperty => "test";
    }

    private class ThrowingJsonConverter : System.Text.Json.Serialization.JsonConverter<string>
    {
        public override string Read(
            ref System.Text.Json.Utf8JsonReader reader,
            Type typeToConvert,
            System.Text.Json.JsonSerializerOptions options
        )
        {
            return "";
        }

        public override void Write(
            System.Text.Json.Utf8JsonWriter writer,
            string value,
            System.Text.Json.JsonSerializerOptions options
        )
        {
            throw new NotSupportedException("Intentional serialization failure");
        }
    }

    [Fact]
    public void EventBusProvider_ConfigureNull_ThrowsArgumentNullException()
    {
        Action act = () => EventBusProvider.Configure(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("eventBus");
    }

    [Fact]
    public async Task InMemoryEventBus_WithNullLogger_DoesNotThrow()
    {
        InMemoryEventBus bus = new(logger: null);
        TestEvent evt = new() { Data = "test" };

        Func<Task> act = () => bus.PublishAsync(evt);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InMemoryEventBus_WithLogger_HandlerThrowsLogsError()
    {
        LogCapture capture = new();
        LoggingCapture logger = new(capture);
        InMemoryEventBus bus = new(logger);

        bus.Subscribe<TestEvent>((_, _) => throw new InvalidOperationException("test error"));

        await bus.PublishAsync(new TestEvent { Data = "test" });

        capture.Messages.Should().ContainSingle();
        capture.Messages[0].Should().Contain("Error:");
        capture.Messages[0].Should().Contain("Event handler for TestEvent failed");
    }

    [Fact]
    public async Task InMemoryEventBus_WithLoggerNoError_DoesNotLogError()
    {
        LogCapture capture = new();
        LoggingCapture logger = new(capture);
        InMemoryEventBus bus = new(logger);

        bus.Subscribe<TestEvent>((_, _) => Task.CompletedTask);

        await bus.PublishAsync(new TestEvent { Data = "test" });

        capture.Messages.Should().BeEmpty("no errors logged");
    }

    [Fact]
    public async Task InMemoryEventBus_CancellationTokenThrowsBeforeHandler_PreventsFurtherHandlers()
    {
        InMemoryEventBus bus = new();
        CancellationTokenSource cts = new();
        List<string> order = [];

        bus.Subscribe<TestEvent>(
            (_, ct) =>
            {
                order.Add("first");
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        );

        bus.Subscribe<TestEvent>(
            (_, _) =>
            {
                order.Add("second");
                return Task.CompletedTask;
            }
        );

        Func<Task> act = () => bus.PublishAsync(new TestEvent(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        order.Should().Equal("first");
    }

    [Fact]
    public async Task InMemoryEventBus_PublishAsync_GenericEventNoSubscribers_DoesNotThrow()
    {
        InMemoryEventBus bus = new();

        Func<Task> act = () => bus.PublishAsync(new TestEvent());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void InMemoryEventBus_Subscribe_WithDelegateReturnsDisposable()
    {
        InMemoryEventBus bus = new();

        IDisposable sub = bus.Subscribe<TestEvent>((_, _) => Task.CompletedTask);

        sub.Should().NotBeNull();
        sub.Should().BeAssignableTo<IDisposable>();
    }

    [Fact]
    public void InMemoryEventBus_Subscribe_WithEventHandlerReturnsDisposable()
    {
        InMemoryEventBus bus = new();
        TestHandler handler = new();

        IDisposable sub = bus.Subscribe(handler);

        sub.Should().NotBeNull();
        sub.Should().BeAssignableTo<IDisposable>();
    }

    [Fact]
    public async Task InMemoryEventBus_DisposeSubscription_ThenDisposeAgain_IsIdempotent()
    {
        InMemoryEventBus bus = new();
        List<TestEvent> received = [];

        IDisposable sub = bus.Subscribe<TestEvent>(
            (evt, _) =>
            {
                received.Add(evt);
                return Task.CompletedTask;
            }
        );

        await bus.PublishAsync(new TestEvent { Data = "first" });
        received.Should().HaveCount(1);

        sub.Dispose();
        await bus.PublishAsync(new TestEvent { Data = "second" });
        received.Should().HaveCount(1);

        sub.Dispose();
        await bus.PublishAsync(new TestEvent { Data = "third" });
        received.Should().HaveCount(1, "double dispose should not affect anything");
    }

    [Fact]
    public async Task InMemoryEventBus_MultipleHandlers_AllReceiveTheSameEvent()
    {
        InMemoryEventBus bus = new();
        TestEvent evt = new() { Data = "shared" };

        List<TestEvent> received1 = [];
        List<TestEvent> received2 = [];
        List<TestEvent> received3 = [];

        bus.Subscribe<TestEvent>(
            (e, _) =>
            {
                received1.Add(e);
                return Task.CompletedTask;
            }
        );

        bus.Subscribe<TestEvent>(
            (e, _) =>
            {
                received2.Add(e);
                return Task.CompletedTask;
            }
        );

        bus.Subscribe<TestEvent>(
            (e, _) =>
            {
                received3.Add(e);
                return Task.CompletedTask;
            }
        );

        await bus.PublishAsync(evt);

        received1.Should().ContainSingle().Which.Should().BeSameAs(evt);
        received2.Should().ContainSingle().Which.Should().BeSameAs(evt);
        received3.Should().ContainSingle().Which.Should().BeSameAs(evt);
    }

    [Fact]
    public async Task InMemoryEventBus_CancellationBetweenHandlers_StopsExecution()
    {
        InMemoryEventBus bus = new();
        CancellationTokenSource cts = new();

        List<string> order = [];
        bus.Subscribe<TestEvent>(
            (_, ct) =>
            {
                order.Add("first");
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        );

        bus.Subscribe<TestEvent>(
            (_, _) =>
            {
                order.Add("second");
                return Task.CompletedTask;
            }
        );

        Func<Task> act = () => bus.PublishAsync(new TestEvent(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        order.Should().Equal("first");
    }

    [Fact]
    public async Task LoggingEventBusDecorator_Constructor_NullInner_Throws()
    {
        Action act = () => new LoggingEventBusDecorator(null!, _ => { });

        act.Should().Throw<ArgumentNullException>().WithParameterName("inner");
    }

    [Fact]
    public async Task LoggingEventBusDecorator_Constructor_NullLog_Throws()
    {
        InMemoryEventBus inner = new();

        Action act = () => new LoggingEventBusDecorator(inner, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("log");
    }

    [Fact]
    public async Task LoggingEventBusDecorator_PublishAsync_EventNotExcluded_Logs()
    {
        InMemoryEventBus inner = new();
        List<string> logs = [];
        LoggingEventBusDecorator decorator = new(
            inner,
            logs.Add,
            excludedEventTypes: ["OtherEvent"]
        );

        await decorator.PublishAsync(new TestEvent { Data = "test" });

        logs.Should().ContainSingle();
        logs[0].Should().Contain("TestEvent");
    }

    [Fact]
    public async Task LoggingEventBusDecorator_PublishAsync_EventExcluded_DoesNotLog()
    {
        InMemoryEventBus inner = new();
        List<string> logs = [];
        LoggingEventBusDecorator decorator = new(
            inner,
            logs.Add,
            excludedEventTypes: ["TestEvent"]
        );

        await decorator.PublishAsync(new TestEvent { Data = "test" });

        logs.Should().BeEmpty();
    }

    [Fact]
    public async Task LoggingEventBusDecorator_PublishAsync_ExcludedListNull_LogsAll()
    {
        InMemoryEventBus inner = new();
        List<string> logs = [];
        LoggingEventBusDecorator decorator = new(inner, logs.Add, excludedEventTypes: null);

        await decorator.PublishAsync(new TestEvent { Data = "test" });

        logs.Should().ContainSingle();
    }

    [Fact]
    public async Task LoggingEventBusDecorator_PublishAsync_EventExcluded_StillDelivers()
    {
        InMemoryEventBus inner = new();
        List<string> logs = [];
        List<TestEvent> received = [];

        LoggingEventBusDecorator decorator = new(
            inner,
            logs.Add,
            excludedEventTypes: ["TestEvent"]
        );
        decorator.Subscribe<TestEvent>(
            (evt, _) =>
            {
                received.Add(evt);
                return Task.CompletedTask;
            }
        );

        await decorator.PublishAsync(new TestEvent { Data = "test" });

        logs.Should().BeEmpty("excluded event should not be logged");
        received.Should().ContainSingle("excluded event should still be delivered");
    }

    [Fact]
    public async Task LoggingEventBusDecorator_Subscribe_Delegate_DelegatesToInner()
    {
        InMemoryEventBus inner = new();
        LoggingEventBusDecorator decorator = new(inner, _ => { });

        List<TestEvent> received = [];
        IDisposable sub = decorator.Subscribe<TestEvent>(
            (evt, _) =>
            {
                received.Add(evt);
                return Task.CompletedTask;
            }
        );

        TestEvent evt = new() { Data = "test" };
        await decorator.PublishAsync(evt);

        received.Should().ContainSingle().Which.Data.Should().Be("test");
        sub.Should().NotBeNull();
    }

    [Fact]
    public async Task LoggingEventBusDecorator_Subscribe_EventHandler_DelegatesToInner()
    {
        InMemoryEventBus inner = new();
        LoggingEventBusDecorator decorator = new(inner, _ => { });

        TestHandler handler = new();
        IDisposable sub = decorator.Subscribe(handler);

        TestEvent evt = new() { Data = "handler-test" };
        await decorator.PublishAsync(evt);

        handler.Received.Should().ContainSingle().Which.Data.Should().Be("handler-test");
        sub.Should().NotBeNull();
    }

    [Fact]
    public async Task LoggingEventBusDecorator_PublishAsync_ExcludedEvents_CaseInsensitive()
    {
        InMemoryEventBus inner = new();
        List<string> logs = [];
        LoggingEventBusDecorator decorator = new(
            inner,
            logs.Add,
            excludedEventTypes: ["testevent"]
        );

        await decorator.PublishAsync(new TestEvent { Data = "test" });

        logs.Should().ContainSingle("exclusion is case-sensitive, so this should be logged");
    }

    [Fact]
    public void AuditingEventBusDecorator_Constructor_NullInner_Throws()
    {
        EventAuditLog auditLog = new();

        Action act = () => new AuditingEventBusDecorator(null!, auditLog);

        act.Should().Throw<ArgumentNullException>().WithParameterName("inner");
    }

    [Fact]
    public void AuditingEventBusDecorator_Constructor_NullAuditLog_Throws()
    {
        InMemoryEventBus inner = new();

        Action act = () => new AuditingEventBusDecorator(inner, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("auditLog");
    }

    [Fact]
    public async Task AuditingEventBusDecorator_PublishAsync_RecordsBeforeDelivering()
    {
        InMemoryEventBus inner = new();
        EventAuditLog auditLog = new();
        AuditingEventBusDecorator decorator = new(inner, auditLog);

        List<int> order = [];
        decorator.Subscribe<TestEvent>(
            (_, _) =>
            {
                order.Add(2);
                return Task.CompletedTask;
            }
        );

        auditLog.Record(new TestEvent(), "TestEvent");
        order.Add(1);

        TestEvent evt = new() { Data = "test" };
        await decorator.PublishAsync(evt);

        auditLog.Count.Should().Be(2);
        order.Should().Equal(1, 2);
    }

    [Fact]
    public async Task AuditingEventBusDecorator_PublishAsync_AuditedEvent_WithDisabledAudit()
    {
        InMemoryEventBus inner = new();
        EventAuditLog auditLog = new(new() { Enabled = false });
        AuditingEventBusDecorator decorator = new(inner, auditLog);

        List<TestEvent> received = [];
        decorator.Subscribe<TestEvent>(
            (evt, _) =>
            {
                received.Add(evt);
                return Task.CompletedTask;
            }
        );

        TestEvent evt = new() { Data = "test" };
        await decorator.PublishAsync(evt);

        auditLog.Count.Should().Be(0, "audit is disabled");
        received.Should().ContainSingle("event should still be delivered");
    }

    [Fact]
    public async Task AuditingEventBusDecorator_PublishAsync_AuditedEvent_WithExclusion()
    {
        InMemoryEventBus inner = new();
        EventAuditLog auditLog = new(new() { ExcludedEventTypes = ["TestEvent"] });
        AuditingEventBusDecorator decorator = new(inner, auditLog);

        List<TestEvent> received = [];
        decorator.Subscribe<TestEvent>(
            (evt, _) =>
            {
                received.Add(evt);
                return Task.CompletedTask;
            }
        );

        TestEvent evt = new() { Data = "test" };
        await decorator.PublishAsync(evt);

        auditLog.Count.Should().Be(0, "TestEvent is excluded from audit");
        received.Should().ContainSingle("event should still be delivered");
    }

    [Fact]
    public async Task AuditingEventBusDecorator_Subscribe_Delegate_DelegatesToInner()
    {
        InMemoryEventBus inner = new();
        EventAuditLog auditLog = new();
        AuditingEventBusDecorator decorator = new(inner, auditLog);

        List<TestEvent> received = [];
        IDisposable sub = decorator.Subscribe<TestEvent>(
            (evt, _) =>
            {
                received.Add(evt);
                return Task.CompletedTask;
            }
        );

        TestEvent evt = new() { Data = "test" };
        await decorator.PublishAsync(evt);

        received.Should().ContainSingle().Which.Data.Should().Be("test");
        sub.Should().NotBeNull();
    }

    [Fact]
    public async Task AuditingEventBusDecorator_Subscribe_EventHandler_DelegatesToInner()
    {
        InMemoryEventBus inner = new();
        EventAuditLog auditLog = new();
        AuditingEventBusDecorator decorator = new(inner, auditLog);

        TestHandler handler = new();
        IDisposable sub = decorator.Subscribe(handler);

        TestEvent evt = new() { Data = "handler-test" };
        await decorator.PublishAsync(evt);

        handler.Received.Should().ContainSingle().Which.Data.Should().Be("handler-test");
        sub.Should().NotBeNull();
    }

    [Fact]
    public void AuditingEventBusDecorator_AuditLog_Property_ReturnsProvidedLog()
    {
        InMemoryEventBus inner = new();
        EventAuditLog auditLog = new();
        AuditingEventBusDecorator decorator = new(inner, auditLog);

        decorator.AuditLog.Should().BeSameAs(auditLog);
    }

    [Fact]
    public async Task InMemoryEventBus_ConcurrentPublish_AllEventDelivered()
    {
        InMemoryEventBus bus = new();
        int successCount = 0;

        bus.Subscribe<TestEvent>(
            (_, _) =>
            {
                Interlocked.Increment(ref successCount);
                return Task.CompletedTask;
            }
        );

        Task[] tasks =
        [
            .. Enumerable
                .Range(0, 50)
                .Select(i => bus.PublishAsync(new TestEvent { Data = $"event-{i}" })),
        ];

        await Task.WhenAll(tasks);

        successCount.Should().Be(50);
    }

    [Fact]
    public async Task InMemoryEventBus_PublishAsync_WithCancellationAfterHandler_RethrowsOperationCanceled()
    {
        InMemoryEventBus bus = new();
        CancellationTokenSource cts = new();

        bus.Subscribe<TestEvent>(
            (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        );

        Func<Task> act = () => bus.PublishAsync(new TestEvent(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task InMemoryEventBus_RemoveSubscriber_DuringIteration_SafeSnapshot()
    {
        InMemoryEventBus bus = new();
        List<string> executed = [];

        IDisposable sub1 = bus.Subscribe<TestEvent>(
            (_, _) =>
            {
                executed.Add("handler1");
                return Task.CompletedTask;
            }
        );

        IDisposable sub2 = bus.Subscribe<TestEvent>(
            (_, _) =>
            {
                executed.Add("handler2");
                sub1.Dispose();
                return Task.CompletedTask;
            }
        );

        IDisposable sub3 = bus.Subscribe<TestEvent>(
            (_, _) =>
            {
                executed.Add("handler3");
                return Task.CompletedTask;
            }
        );

        await bus.PublishAsync(new TestEvent());

        executed.Should().Equal("handler1", "handler2", "handler3");
    }

    [Fact]
    public async Task LoggingEventBusDecorator_LogFormat_ContainsAllRequiredFields()
    {
        InMemoryEventBus inner = new();
        List<string> logs = [];
        LoggingEventBusDecorator decorator = new(inner, logs.Add);

        TestEvent evt = new() { Data = "test" };
        await decorator.PublishAsync(evt);

        logs.Should().ContainSingle();
        string log = logs[0];

        log.Should().StartWith("[Event]");
        log.Should().Contain("TestEvent");
        log.Should().Contain("Source=Test");
        log.Should().Contain($"EventId={evt.EventId}");
        log.Should().Contain("Timestamp=");
    }

    [Fact]
    public async Task EventAuditLog_Enabled_DefaultTrue()
    {
        EventAuditLog log = new();

        log.Enabled.Should().BeTrue();
    }

    [Fact]
    public void EventAuditLog_WithOptions_RespectsOptions()
    {
        EventAuditOptions options = new()
        {
            Enabled = false,
            MaxEntries = 5000,
            CompactionPercentage = 0.5,
        };

        EventAuditLog log = new(options);

        log.Enabled.Should().BeFalse();
    }

    [Fact]
    public void EventAuditLog_Compact_RemovesOldestEntries()
    {
        EventAuditLog log = new(new() { MaxEntries = 10, CompactionPercentage = 0.5 });

        for (int i = 0; i < 12; i++)
        {
            log.Record(new TestEvent { Data = i.ToString() }, "TestEvent");
        }

        log.Count.Should().BeLessThanOrEqualTo(12);
        log.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EventAuditLog_GetEntries_AllEntries()
    {
        EventAuditLog log = new();

        log.Record(new TestEvent { Data = "1" }, "TestEvent");
        log.Record(new TestEvent { Data = "2" }, "TestEvent");

        IReadOnlyList<EventAuditEntry> entries = log.GetEntries();

        entries.Should().HaveCount(2);
    }

    [Fact]
    public void EventAuditLog_GetEntries_ByType()
    {
        EventAuditLog log = new();

        log.Record(new TestEvent { Data = "1" }, "TestEvent");
        log.Record(new TestEvent { Data = "2" }, "OtherEvent");

        IReadOnlyList<EventAuditEntry> entries = log.GetEntries("TestEvent");

        entries.Should().ContainSingle();
        entries[0].EventType.Should().Be("TestEvent");
    }

    [Fact]
    public void EventAuditLog_GetEntries_ByTimeRange()
    {
        EventAuditLog log = new();
        DateTime before = DateTime.UtcNow.AddSeconds(-1);

        log.Record(new TestEvent { Data = "1" }, "TestEvent");

        DateTime after = DateTime.UtcNow.AddSeconds(1);

        IReadOnlyList<EventAuditEntry> entries = log.GetEntries(before, after);

        entries.Should().ContainSingle();
    }

    [Fact]
    public void EventAuditLog_Clear_RemovesAllAndResetsCount()
    {
        EventAuditLog log = new();

        log.Record(new TestEvent { Data = "1" }, "TestEvent");
        log.Record(new TestEvent { Data = "2" }, "TestEvent");

        log.Count.Should().Be(2);

        log.Clear();

        log.Count.Should().Be(0);
        log.GetEntries().Should().BeEmpty();
    }

    [Fact]
    public void EventAuditLog_SerializationFailure_FallbackPayload()
    {
        EventAuditLog log = new();

        SerializationThrowingEvent evt = new();
        log.Record(evt, "SerializationThrowingEvent");

        EventAuditEntry entry = log.GetEntries()[0];
        entry.Payload.Should().Contain("\"EventId\":");
        entry.Payload.Should().NotBeNull();
    }
}
