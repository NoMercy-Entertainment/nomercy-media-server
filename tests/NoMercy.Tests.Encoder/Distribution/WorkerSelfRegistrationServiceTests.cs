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

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using NoMercy.Encoder.Hardware;

namespace NoMercy.Tests.Encoder.Distribution;

public class WorkerSelfRegistrationServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenDisabled_ExitsImmediately()
    {
        // No CoordinatorUrl → service must no-op and exit cleanly without
        // hitting the network. Useful on standalone installs where the
        // hosted service is registered but shouldn't activate.
        RecordingHandler handler = new();
        WorkerSelfRegistrationService sut = MakeService(
            handler,
            opts =>
            {
                opts.DistributedEncodingSigningKey = "key";
                // CoordinatorUrl intentionally not set.
            }
        );

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));
        await sut.StartAsync(cts.Token);
        await sut.StopAsync(CancellationToken.None);

        handler.RequestLog.Should().BeEmpty();
    }

    // CoordinatorUrl now comes from configuration, so a typo must not take the
    // whole server down: new Uri() threw inside ExecuteAsync, and a faulted
    // ExecuteAsync stops the host (BackgroundServiceExceptionBehavior.StopHost).
    [Theory]
    [InlineData("not a url")]
    [InlineData("coordinator.example:7626")]
    [InlineData("ftp://coordinator.example")]
    public async Task ExecuteAsync_CoordinatorUrlNotAnHttpUrl_StaysOffWithoutThrowing(
        string coordinatorUrl
    )
    {
        RecordingHandler handler = new();
        WorkerSelfRegistrationService sut = MakeService(
            handler,
            opts =>
            {
                opts.DistributedEncodingSigningKey = "key";
                opts.CoordinatorUrl = coordinatorUrl;
                opts.WorkerSelfBaseUrl = "http://worker.test";
            }
        );

        await sut.StartAsync(CancellationToken.None);
        Func<Task> run = () => sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Should().NotThrowAsync("the service must log and stay off, not fault");
        await sut.StopAsync(CancellationToken.None);

        handler.RequestLog.Should().BeEmpty();
    }

    // The id travels in the heartbeat and unregister paths; anything outside
    // [A-Za-z0-9._-] would need escaping there, and "." or ".." would be collapsed.
    [Theory]
    [InlineData("rack 2")]
    [InlineData("ünit")]
    [InlineData("a/b")]
    [InlineData("..")]
    public async Task ExecuteAsync_WorkerIdNotUrlSafe_StaysOffWithoutThrowing(string workerId)
    {
        RecordingHandler handler = new();
        WorkerSelfRegistrationService sut = MakeService(
            handler,
            opts =>
            {
                opts.DistributedEncodingSigningKey = "key";
                opts.CoordinatorUrl = "http://coordinator.test";
                opts.WorkerSelfBaseUrl = "http://worker.test";
                opts.WorkerId = workerId;
            }
        );

        await sut.StartAsync(CancellationToken.None);
        Func<Task> run = () => sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Should().NotThrowAsync("the service must log and stay off, not fault");
        await sut.StopAsync(CancellationToken.None);

        handler.RequestLog.Should().BeEmpty();
    }

    // Without a trailing slash, resolving "api/v1/..." against "/nomercy" would
    // drop the prefix and miss the coordinator.
    [Fact]
    public async Task ExecuteAsync_CoordinatorUnderAPathPrefix_KeepsThePrefix()
    {
        TaskCompletionSource registerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingHandler handler = new(respond: req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/register", StringComparison.Ordinal))
                registerSeen.TrySetResult();
            return new(HttpStatusCode.OK);
        });
        WorkerSelfRegistrationService sut = MakeService(
            handler,
            opts =>
            {
                opts.DistributedEncodingSigningKey = "key";
                opts.CoordinatorUrl = "http://coordinator.test/nomercy";
                opts.WorkerSelfBaseUrl = "http://worker.test";
                opts.WorkerId = "tw";
                opts.WorkerHeartbeatInterval = TimeSpan.FromMinutes(5);
            }
        );

        using CancellationTokenSource cts = new();
        await sut.StartAsync(cts.Token);
        await registerSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await sut.StopAsync(CancellationToken.None);

        handler
            .RequestLog.Should()
            .Contain(r => r.Path == "/nomercy/api/v1/distribution/workers/register");
    }

    [Fact]
    public async Task ExecuteAsync_CoordinatorUrlWithAPassword_NeverLogsIt()
    {
        TaskCompletionSource registerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingHandler handler = new(respond: req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/register", StringComparison.Ordinal))
                registerSeen.TrySetResult();
            return new(HttpStatusCode.OK);
        });
        CapturingLogger logger = new();
        WorkerSelfRegistrationService sut = MakeService(
            handler,
            opts =>
            {
                opts.DistributedEncodingSigningKey = "key";
                opts.CoordinatorUrl = "http://operator:s3cret@coordinator.test";
                opts.WorkerSelfBaseUrl = "http://worker.test";
                opts.WorkerId = "tw";
                opts.WorkerHeartbeatInterval = TimeSpan.FromMinutes(5);
            },
            logger
        );

        using CancellationTokenSource cts = new();
        await sut.StartAsync(cts.Token);
        await registerSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await sut.StopAsync(CancellationToken.None);

        logger.Messages.Should().Contain(m => m.Contains("http://coordinator.test/"));
        logger.Messages.Should().NotContain(m => m.Contains("s3cret") || m.Contains("operator"));
    }

    [Fact]
    public async Task ExecuteAsync_Enabled_PostsRegistrationOnStart()
    {
        TaskCompletionSource registerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingHandler handler = new(respond: req =>
        {
            if (req.RequestUri!.ToString().Contains("register"))
                registerSeen.TrySetResult();
            return new(HttpStatusCode.OK);
        });

        WorkerSelfRegistrationService sut = MakeService(
            handler,
            opts =>
            {
                opts.DistributedEncodingSigningKey = "key";
                opts.CoordinatorUrl = "http://coordinator.test";
                opts.WorkerSelfBaseUrl = "http://worker.test";
                opts.WorkerId = "test-worker";
                opts.WorkerHeartbeatInterval = TimeSpan.FromMilliseconds(50);
            }
        );

        using CancellationTokenSource cts = new();
        await sut.StartAsync(cts.Token);

        // Deterministic wait: signal fires when register is observed.
        await registerSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        await sut.StopAsync(CancellationToken.None);

        handler.RequestLog.Should().Contain(r => r.Path.Contains("register"));
    }

    [Fact]
    public async Task ExecuteAsync_Shutdown_SendsUnregisterDelete()
    {
        TaskCompletionSource registerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingHandler handler = new(respond: req =>
        {
            if (req.RequestUri!.ToString().Contains("register") && req.Method == HttpMethod.Post)
                registerSeen.TrySetResult();
            return new(HttpStatusCode.OK);
        });

        WorkerSelfRegistrationService sut = MakeService(
            handler,
            opts =>
            {
                opts.DistributedEncodingSigningKey = "key";
                opts.CoordinatorUrl = "http://coordinator.test";
                opts.WorkerSelfBaseUrl = "http://worker.test";
                opts.WorkerId = "tw";
                opts.WorkerHeartbeatInterval = TimeSpan.FromMilliseconds(50);
            }
        );

        using CancellationTokenSource cts = new();
        await sut.StartAsync(cts.Token);
        await registerSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        handler
            .RequestLog.Should()
            .Contain(r =>
                r.Method == HttpMethod.Delete && r.Path.Contains("/distribution/workers/tw")
            );
    }

    [Fact]
    public async Task ExecuteAsync_HeartbeatRejected_TriggersReregistration()
    {
        // Heartbeat returning 404 means "coordinator doesn't know us".
        // Service must retry register instead of staying in a 404 loop.
        int registerCount = 0;
        TaskCompletionSource secondRegister = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        RecordingHandler handler = new(respond: req =>
        {
            if (req.RequestUri!.ToString().Contains("register"))
            {
                int n = Interlocked.Increment(ref registerCount);
                if (n >= 2)
                    secondRegister.TrySetResult();
                return new(HttpStatusCode.OK);
            }
            if (req.RequestUri.ToString().Contains("heartbeat"))
                return new(HttpStatusCode.NotFound);
            return new(HttpStatusCode.OK);
        });

        WorkerSelfRegistrationService sut = MakeService(
            handler,
            opts =>
            {
                opts.DistributedEncodingSigningKey = "key";
                opts.CoordinatorUrl = "http://coordinator.test";
                opts.WorkerSelfBaseUrl = "http://worker.test";
                opts.WorkerId = "tw";
                opts.WorkerHeartbeatInterval = TimeSpan.FromMilliseconds(30);
            }
        );

        using CancellationTokenSource cts = new();
        await sut.StartAsync(cts.Token);
        await secondRegister.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        await sut.StopAsync(CancellationToken.None);

        Volatile
            .Read(ref registerCount)
            .Should()
            .BeGreaterThan(1, "heartbeat 404 must trigger re-registration");
    }

    [Fact]
    public async Task ExecuteAsync_WhenRegisterTimesOut_SurvivesAndKeepsHeartbeating()
    {
        // An HttpClient timeout surfaces as a TaskCanceledException, which IS an
        // OperationCanceledException. It must not escape TryRegisterAsync
        // uncaught — the service's own stoppingToken was never asked to
        // cancel, so the ExecuteAsync loop must carry on to the heartbeat.
        TaskCompletionSource heartbeatSeen = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        RecordingHandler handler = new(respond: req =>
        {
            if (req.RequestUri!.ToString().Contains("register"))
                throw new TaskCanceledException("timed out", new TimeoutException());

            if (req.RequestUri.ToString().Contains("heartbeat"))
                heartbeatSeen.TrySetResult();

            return new(HttpStatusCode.OK);
        });

        WorkerSelfRegistrationService sut = MakeService(
            handler,
            opts =>
            {
                opts.DistributedEncodingSigningKey = "key";
                opts.CoordinatorUrl = "http://coordinator.test";
                opts.WorkerSelfBaseUrl = "http://worker.test";
                opts.WorkerId = "tw";
                opts.WorkerHeartbeatInterval = TimeSpan.FromMilliseconds(30);
            }
        );

        using CancellationTokenSource cts = new();
        await sut.StartAsync(cts.Token);

        // Proves the loop survived the register timeout: it reached the next
        // heartbeat instead of the ExecuteAsync task faulting.
        await heartbeatSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();

        // Before the fix, ExecuteAsync's Task was faulted by the escaped
        // TaskCanceledException, and BackgroundService.StopAsync awaits (and
        // rethrows from) that task — this call would throw.
        Func<Task> stop = () => sut.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static WorkerSelfRegistrationService MakeService(
        RecordingHandler handler,
        Action<EncoderOptions> configure,
        ILogger<WorkerSelfRegistrationService>? logger = null
    )
    {
        EncoderOptions opts = new();
        configure(opts);

        ServiceCollection services = new();
        services.AddSingleton(opts);
        services.AddSingleton<IHardwareCapabilities>(new HardwareCapabilities([], 4));
        services
            .AddHttpClient()
            .ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => handler));

        ServiceProvider provider = services.BuildServiceProvider();

        return new(
            provider.GetRequiredService<IHardwareCapabilities>(),
            opts,
            provider.GetRequiredService<IHttpClientFactory>(),
            logger ?? NullLogger<WorkerSelfRegistrationService>.Instance
        );
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private readonly List<(HttpMethod Method, string Path)> _log = [];

        public RecordingHandler(HashSet<string>? okOn = null)
            : this(req => new(ShouldOk(req, okOn) ? HttpStatusCode.OK : HttpStatusCode.NotFound))
        { }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public IReadOnlyList<(HttpMethod Method, string Path)> RequestLog
        {
            get
            {
                lock (_log)
                    return [.. _log];
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_log)
                _log.Add((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(_respond(request));
        }

        private static bool ShouldOk(HttpRequestMessage req, HashSet<string>? okOn)
        {
            if (okOn is null)
                return true;
            string path = req.RequestUri!.AbsolutePath;
            return okOn.Any(pattern => path.Contains(pattern));
        }
    }

    private sealed class CapturingLogger : ILogger<WorkerSelfRegistrationService>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_messages)
                _messages.Add(formatter(state, exception));
        }
    }
}
