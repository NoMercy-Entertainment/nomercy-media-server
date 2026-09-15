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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Connectivity;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Status;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: ConnectivityManager's IHostedService lifecycle must (1) wait
/// for boot to finish and for authentication before evaluating strategies,
/// (2) give up cleanly (no throw, no hang) when cancelled while waiting, (3)
/// run the full evaluate-and-connect flow once boot+auth are ready, and (4)
/// tear down the active strategy on stop/dispose so a stopped server never
/// leaves a tunnel or port mapping dangling.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectivityManagerLifecycleTests : IDisposable
{
    private readonly ConnectivityMode _originalMode = RuntimeServerSettings
        .Current
        .ConnectivityMode;

    // Most fixtures in this file exercise strategy evaluation, which requires Auto —
    // RuntimeServerSettings.Current is a process-wide static, and its default is now
    // LocalOnly (a fresh install must not attempt remote connectivity unasked), so every
    // test here that isn't specifically testing the LocalOnly-pinned path needs Auto
    // restored explicitly rather than inheriting whatever an earlier test left behind.
    public ConnectivityManagerLifecycleTests()
    {
        RuntimeServerSettings.Current.ConnectivityMode = ConnectivityMode.Auto;
    }

    public void Dispose()
    {
        RuntimeServerSettings.Current.ConnectivityMode = _originalMode;
    }

    private sealed class FastNetworkDiscovery : INetworkDiscovery
    {
        public int DiscoverCallCount { get; private set; }
        public string InternalIp { get; set; } = "192.168.1.1";
        public string RegistrationInternalIp => InternalIp;
        public string ExternalIp { get; set; } = "1.2.3.4";
        public string? InternalIpV6 => null;
        public string? ExternalIpV6 { get; set; }
        public string InternalDomain => string.Empty;
        public string InternalAddress => string.Empty;
        public string ExternalDomain => string.Empty;
        public string ExternalAddress => string.Empty;
        public string DirectExternalAddress => ExternalAddress;
        public string? ExternalAddressV6 => null;
        public bool Ipv6Enabled => false;

        public Task DiscoverExternalIpAsync()
        {
            DiscoverCallCount++;
            return Task.CompletedTask;
        }

        public Task ForceRediscoveryAsync() => Task.CompletedTask;

        public Task<bool> IsPortOpenAsync() => Task.FromResult(false);

        public Task RemovePortMappingsAsync() => Task.CompletedTask;
    }

    private sealed class RecordingStrategy(bool succeeds) : IConnectivityStrategy
    {
        public bool TeardownCalled { get; private set; }
        public string Name => "Recording";
        public int Priority => 1;
        public ConnectivityType Type => ConnectivityType.PortForward;

        public Task<ConnectivityResult> TryEstablishAsync(CancellationToken ct) =>
            Task.FromResult(succeeds ? ConnectivityResult.Verified() : ConnectivityResult.Failed());

        public Task TeardownAsync()
        {
            TeardownCalled = true;
            return Task.CompletedTask;
        }
    }

    private static ConnectivityManager BuildManager(
        FastNetworkDiscovery discovery,
        BootStatus boot,
        AuthTokenStore tokenStore,
        params IConnectivityStrategy[] strategies
    )
    {
        return new(
            NullLogger<ConnectivityManager>.Instance,
            tokenStore,
            discovery,
            strategies,
            boot,
            new ConnectivityStatus()
        );
    }

    /// <summary>
    /// Fails the first N attempts, then succeeds. Models the real case that stranded CGNAT
    /// servers: cloudflared is still downloading when connectivity is first evaluated, so
    /// the only viable transport cannot start yet.
    /// </summary>
    private sealed class EventuallySucceedingStrategy(int failuresBeforeSuccess)
        : IConnectivityStrategy
    {
        public int Attempts { get; private set; }
        public string Name => "EventuallySucceeding";
        public int Priority => 1;
        public ConnectivityType Type => ConnectivityType.CloudflareTunnel;

        public Task<ConnectivityResult> TryEstablishAsync(CancellationToken ct)
        {
            Attempts++;
            return Task.FromResult(
                Attempts > failuresBeforeSuccess
                    ? ConnectivityResult.Verified()
                    : ConnectivityResult.Failed()
            );
        }

        public Task TeardownAsync() => Task.CompletedTask;
    }

    private sealed class DyingStrategy : IConnectivityStrategy
    {
        public int Attempts { get; private set; }
        public bool Alive { get; set; } = true;
        public string Name => "Dying";
        public int Priority => 1;
        public ConnectivityType Type => ConnectivityType.CloudflareTunnel;
        public bool IsStillEstablished => Alive;

        public Task<ConnectivityResult> TryEstablishAsync(CancellationToken ct)
        {
            Attempts++;
            Alive = true;
            return Task.FromResult(ConnectivityResult.Verified());
        }

        public Task TeardownAsync() => Task.CompletedTask;
    }

    private static ConnectivityManager BuildSupervisedManager(
        params IConnectivityStrategy[] strategies
    )
    {
        BootStatus boot = new();
        boot.MarkStarted();
        AuthTokenStore tokenStore = new();
        tokenStore.SetAccessToken("test-token");

        return new(
            NullLogger<ConnectivityManager>.Instance,
            tokenStore,
            new FastNetworkDiscovery(),
            strategies,
            boot,
            new ConnectivityStatus(),
            null,
            TimeSpan.FromMilliseconds(10)
        );
    }

    [Fact]
    public async Task Supervision_RecoversWithoutARestart_WhenTheTransportOnlyWorksLater()
    {
        EventuallySucceedingStrategy strategy = new(failuresBeforeSuccess: 2);
        ConnectivityManager manager = BuildSupervisedManager(strategy);

        await manager.StartAsync(CancellationToken.None);

        for (int i = 0; i < 200 && manager.CurrentState != ConnectivityState.Tunneled; i++)
            await Task.Delay(20);

        // Evaluating exactly once per process made a transient cause permanent: a server
        // whose only transport was not ready yet stayed local-only for its whole uptime.
        Assert.Equal(ConnectivityState.Tunneled, manager.CurrentState);
        Assert.True(strategy.Attempts >= 3);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Supervision_ReEstablishes_WhenAnActiveTransportDies()
    {
        DyingStrategy strategy = new();
        ConnectivityManager manager = BuildSupervisedManager(strategy);

        await manager.StartAsync(CancellationToken.None);
        for (int i = 0; i < 200 && manager.CurrentState != ConnectivityState.Tunneled; i++)
            await Task.Delay(20);

        int attemptsWhenUp = strategy.Attempts;
        strategy.Alive = false;

        for (int i = 0; i < 200 && strategy.Attempts == attemptsWhenUp; i++)
            await Task.Delay(20);

        Assert.True(strategy.Attempts > attemptsWhenUp);
        Assert.Equal(ConnectivityState.Tunneled, manager.CurrentState);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_BootAndAuthAlreadyReady_RunsEvaluateAsync()
    {
        FastNetworkDiscovery discovery = new();
        BootStatus boot = new();
        boot.MarkStarted();
        AuthTokenStore tokenStore = new();
        tokenStore.SetAccessToken("test-token");
        RecordingStrategy strategy = new(succeeds: true);
        ConnectivityManager manager = BuildManager(discovery, boot, tokenStore, strategy);

        await manager.StartAsync(CancellationToken.None);

        // ExecuteAsync runs on the background task; give it a moment to reach
        // EvaluateAsync (no real I/O — this is a fast in-process transition).
        for (int i = 0; i < 50 && manager.CurrentState != ConnectivityState.DirectAccess; i++)
            await Task.Delay(20);

        Assert.Equal(ConnectivityState.DirectAccess, manager.CurrentState);
        Assert.Equal(1, discovery.DiscoverCallCount);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_NoAuthToken_StopAsync_CancelsWaitLoop_WithoutThrowing()
    {
        FastNetworkDiscovery discovery = new();
        BootStatus boot = new();
        boot.MarkStarted();
        AuthTokenStore tokenStore = new(); // AccessToken stays null
        ConnectivityManager manager = BuildManager(discovery, boot, tokenStore);

        await manager.StartAsync(CancellationToken.None);
        await Task.Delay(50); // let ExecuteAsync enter the "waiting for auth" loop

        Exception? ex = await Record.ExceptionAsync(() =>
            manager.StopAsync(CancellationToken.None)
        );

        Assert.Null(ex);
        // Never reached DiscoverExternalIpAsync — auth never arrived.
        Assert.Equal(0, discovery.DiscoverCallCount);
    }

    [Fact]
    public async Task StartAsync_AuthArrivesLate_StillEvaluatesConnectivity()
    {
        // The 30s-and-give-up behaviour used to `return` out of ExecuteAsync entirely,
        // so a server whose operator signed in slower than 30s stayed local-only for the
        // rest of the process's life. This proves it instead keeps polling and re-enters
        // evaluation the moment a token shows up, however late.
        FastNetworkDiscovery discovery = new();
        BootStatus boot = new();
        boot.MarkStarted();
        AuthTokenStore tokenStore = new(); // AccessToken starts null
        RecordingStrategy strategy = new(succeeds: true);
        ConnectivityManager manager = BuildManager(discovery, boot, tokenStore, strategy);

        await manager.StartAsync(CancellationToken.None);

        // Give the wait loop a couple of real poll iterations with no token before the
        // token shows up — proving it did not give up after the first check.
        await Task.Delay(1500);
        Assert.Equal(0, discovery.DiscoverCallCount);

        tokenStore.SetAccessToken("late-token");

        for (int i = 0; i < 100 && discovery.DiscoverCallCount == 0; i++)
            await Task.Delay(50);

        Assert.Equal(1, discovery.DiscoverCallCount);
        Assert.Equal(ConnectivityState.DirectAccess, manager.CurrentState);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_BootNeverStarted_StopAsync_CancelsWaitLoop_WithoutThrowing()
    {
        FastNetworkDiscovery discovery = new();
        BootStatus boot = new(); // never marked started
        AuthTokenStore tokenStore = new();
        tokenStore.SetAccessToken("test-token");
        ConnectivityManager manager = BuildManager(discovery, boot, tokenStore);

        await manager.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        Exception? ex = await Record.ExceptionAsync(() =>
            manager.StopAsync(CancellationToken.None)
        );

        Assert.Null(ex);
        Assert.Equal(0, discovery.DiscoverCallCount);
    }

    [Fact]
    public async Task StopAsync_WhenNeverStarted_ReturnsImmediately_WithoutThrowing()
    {
        FastNetworkDiscovery discovery = new();
        BootStatus boot = new();
        AuthTokenStore tokenStore = new();
        ConnectivityManager manager = BuildManager(discovery, boot, tokenStore);

        Exception? ex = await Record.ExceptionAsync(() =>
            manager.StopAsync(CancellationToken.None)
        );

        Assert.Null(ex);
    }

    [Fact]
    public async Task StopAsync_AfterSuccessfulEvaluate_TearsDownActiveStrategy()
    {
        // TeardownAsync on the active strategy only runs when StopAsync has an
        // _executingTask to wait on — i.e. only after StartAsync (not when
        // EvaluateAsync is invoked directly). Drive the real lifecycle so this
        // proves the actual StartAsync → EvaluateAsync → StopAsync path tears
        // down, matching what the hosted-service shutdown sequence does.
        FastNetworkDiscovery discovery = new();
        BootStatus boot = new();
        boot.MarkStarted();
        AuthTokenStore tokenStore = new();
        tokenStore.SetAccessToken("test-token");
        RecordingStrategy strategy = new(succeeds: true);
        ConnectivityManager manager = BuildManager(discovery, boot, tokenStore, strategy);

        await manager.StartAsync(CancellationToken.None);
        for (int i = 0; i < 50 && manager.CurrentState != ConnectivityState.DirectAccess; i++)
            await Task.Delay(20);

        await manager.StopAsync(CancellationToken.None);

        Assert.True(strategy.TeardownCalled);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        FastNetworkDiscovery discovery = new();
        BootStatus boot = new();
        AuthTokenStore tokenStore = new();
        ConnectivityManager manager = BuildManager(discovery, boot, tokenStore);

        Exception? ex = Record.Exception(() =>
        {
            manager.Dispose();
            manager.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact]
    public void ActiveStrategy_BeforeAnyEvaluate_IsLocalOnly()
    {
        FastNetworkDiscovery discovery = new();
        BootStatus boot = new();
        AuthTokenStore tokenStore = new();
        ConnectivityManager manager = BuildManager(discovery, boot, tokenStore);

        Assert.Equal(ConnectivityType.LocalOnly, manager.ActiveStrategy);
    }

    [Fact]
    public void CurrentState_BeforeAnyEvaluate_IsStarting()
    {
        FastNetworkDiscovery discovery = new();
        BootStatus boot = new();
        AuthTokenStore tokenStore = new();
        ConnectivityManager manager = BuildManager(discovery, boot, tokenStore);

        Assert.Equal(ConnectivityState.Starting, manager.CurrentState);
    }

    // ── LocalOnly's two meanings ─────────────────────────────────────────────
    //
    // LocalOnly is set both when the operator pins the mode (a real terminal choice) and when
    // nothing worked this pass (not terminal). Backing off and logging "retrying" for the
    // pinned case implied the server was still hunting for connectivity when it had
    // deliberately stopped looking.

    private sealed class CapturingLogger : ILogger<ConnectivityManager>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (Messages)
                Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }

    [Fact]
    public async Task Supervision_ModePinnedToLocalOnly_ReEvaluatesPeriodically_ButNeverLogsRetryBackoff()
    {
        ConnectivityMode previous = RuntimeServerSettings.Current.ConnectivityMode;
        RuntimeServerSettings.Current.ConnectivityMode = ConnectivityMode.LocalOnly;

        try
        {
            CapturingLogger logger = new();
            BootStatus boot = new();
            boot.MarkStarted();
            AuthTokenStore tokenStore = new();
            tokenStore.SetAccessToken("test-token");

            ConnectivityManager manager = new(
                logger,
                tokenStore,
                new FastNetworkDiscovery(),
                [],
                boot,
                new ConnectivityStatus(),
                null,
                TimeSpan.FromMilliseconds(10)
            );

            await manager.StartAsync(CancellationToken.None);
            await Task.Delay(200);
            await manager.StopAsync(CancellationToken.None);

            Assert.Equal(ConnectivityState.LocalOnly, manager.CurrentState);

            int pinnedReEvaluations = logger.Messages.Count(m => m.Contains("pinned to LocalOnly"));
            Assert.True(
                pinnedReEvaluations > 1,
                $"Expected more than one re-evaluation while pinned, got {pinnedReEvaluations}."
            );
            Assert.DoesNotContain(logger.Messages, m => m.Contains("retrying in"));
        }
        finally
        {
            RuntimeServerSettings.Current.ConnectivityMode = previous;
        }
    }

    [Fact]
    public async Task Supervision_LocalOnlyBecauseNothingWorked_LogsRetryBackoff()
    {
        BootStatus boot = new();
        boot.MarkStarted();
        AuthTokenStore tokenStore = new();
        tokenStore.SetAccessToken("test-token");
        CapturingLogger logger = new();
        RecordingStrategy failing = new(succeeds: false);

        ConnectivityManager manager = new(
            logger,
            tokenStore,
            new FastNetworkDiscovery(),
            [failing],
            boot,
            new ConnectivityStatus(),
            null,
            TimeSpan.FromMilliseconds(10)
        );

        await manager.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(ConnectivityState.LocalOnly, manager.CurrentState);
        Assert.Contains(logger.Messages, m => m.Contains("retrying in"));
    }
}
