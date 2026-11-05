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

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Connectivity;
using NoMercy.Networking.Connectivity.Strategies;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Status;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed class CloudflareTunnelStrategyTests
{
    // See QuickTunnelStrategyTests.StartLongRunningProcess: a real process is needed
    // because IsStillEstablished reads Process.HasExited.
    private static Process StartLongRunningProcess()
    {
        ProcessStartInfo info = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new("ping", "-n 30 127.0.0.1")
            : new("sleep", "30");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        return Process.Start(info)!;
    }

    /// <summary>
    /// Records every level a call was logged at. NullLogger cannot answer "did this go out as
    /// a Warning or a Debug" — the exact question the shutdown-noise fix lives or dies on.
    /// </summary>
    private sealed class CapturingLogger : ILogger<CloudflareTunnelStrategy>
    {
        public List<LogLevel> Levels { get; } = [];

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
            Levels.Add(logLevel);
        }
    }

    private static CloudflareTunnelStrategy BuildStrategy(
        ConnectivityStatus status,
        Func<Task>? checkAvailability = null,
        Func<bool>? binaryExists = null
    )
    {
        return new(
            NullLogger<CloudflareTunnelStrategy>.Instance,
            status,
            checkAvailability,
            binaryExists
        );
    }

    private static (
        CloudflareTunnelStrategy Strategy,
        CapturingLogger Logger
    ) BuildCapturingStrategy(ConnectivityStatus status)
    {
        CapturingLogger logger = new();
        CloudflareTunnelStrategy strategy = new(logger, status);
        return (strategy, logger);
    }

    // ── IsReady (precondition) ───────────────────────────────────────────────
    //
    // The defect: TryEstablishAsync was the only way to learn the binary was missing, so a
    // missing download and a genuine tunnel failure returned the same Failed() shape and the
    // manager could not tell "not yet" from "no". IsReady is synchronous and side-effect-free
    // so the manager can defer without spending an attempt.

    [Fact]
    public void IsReady_WhenNoTokenIsAssigned_IsTrueRegardlessOfTheBinary()
    {
        ConnectivityStatus status = new() { CloudflareTunnelToken = null };
        CloudflareTunnelStrategy strategy = BuildStrategy(status, binaryExists: () => false);

        Assert.True(strategy.IsReady);
    }

    [Fact]
    public void IsReady_WhenTokenAssigned_ButBinaryMissing_IsFalse()
    {
        ConnectivityStatus status = new() { CloudflareTunnelToken = "assigned-token" };
        CloudflareTunnelStrategy strategy = BuildStrategy(status, binaryExists: () => false);

        Assert.False(strategy.IsReady);
    }

    [Fact]
    public void IsReady_WhenTokenAssigned_AndBinaryPresent_IsTrue()
    {
        ConnectivityStatus status = new() { CloudflareTunnelToken = "assigned-token" };
        CloudflareTunnelStrategy strategy = BuildStrategy(status, binaryExists: () => true);

        Assert.True(strategy.IsReady);
    }

    [Fact]
    public async Task TryEstablishAsync_UsesTheSameInjectedBinaryCheckAsIsReady()
    {
        bool binaryPresent = false;
        ConnectivityStatus status = new() { CloudflareTunnelToken = "assigned-token" };
        CloudflareTunnelStrategy strategy = BuildStrategy(
            status,
            binaryExists: () => binaryPresent
        );

        Assert.False(strategy.IsReady);
        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);
        Assert.False(result.Established);

        binaryPresent = true;
        Assert.True(strategy.IsReady);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTokenIsNull_ReturnsFalse()
    {
        ConnectivityStatus status = new() { CloudflareTunnelToken = null };
        CloudflareTunnelStrategy strategy = BuildStrategy(status);

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.False(result.Established);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTokenIsEmpty_ReturnsFalse()
    {
        ConnectivityStatus status = new() { CloudflareTunnelToken = string.Empty };
        CloudflareTunnelStrategy strategy = BuildStrategy(status);

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.False(result.Established);
    }

    [Fact]
    public void ReapOrphanedTunnels_LeavesUnrelatedCloudflaredProcessesAlone()
    {
        ConnectivityStatus status = new();
        CloudflareTunnelStrategy strategy = BuildStrategy(status);

        // Nothing on this machine runs from our dependencies path during the test run, so a
        // correct reaper is a no-op here. The assertion that matters is that it does not
        // throw and does not go killing every cloudflared it can see — a user running their
        // own tunnel for something unrelated must not have it stopped by us.
        Exception? ex = Record.Exception(strategy.ReapOrphanedTunnels);

        Assert.Null(ex);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTokenIsNull_DoesNotSetNatStatusToTunneled()
    {
        ConnectivityStatus status = new()
        {
            CloudflareTunnelToken = null,
            NatStatus = NatStatus.None,
        };
        CloudflareTunnelStrategy strategy = BuildStrategy(status);

        await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.NotEqual(NatStatus.Tunneled, status.NatStatus);
    }

    [Fact]
    public async Task TryEstablishAsync_CheckAvailabilityCallback_IsInvokedBeforeTokenCheck()
    {
        bool called = false;
        ConnectivityStatus status = new() { CloudflareTunnelToken = null };
        CloudflareTunnelStrategy strategy = BuildStrategy(
            status,
            checkAvailability: () =>
            {
                called = true;
                return Task.CompletedTask;
            }
        );

        await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.True(called);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTokenSet_ButBinaryMissing_ReturnsFalse()
    {
        ConnectivityStatus status = new() { CloudflareTunnelToken = "dummy-tunnel-token" };
        CloudflareTunnelStrategy strategy = BuildStrategy(status);

        ConnectivityResult result = await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.False(result.Established);
    }

    [Fact]
    public async Task TryEstablishAsync_WhenTokenSet_ButBinaryMissing_NatStatusNotTunneled()
    {
        ConnectivityStatus status = new()
        {
            CloudflareTunnelToken = "dummy-token",
            NatStatus = NatStatus.None,
        };
        CloudflareTunnelStrategy strategy = BuildStrategy(status);

        await strategy.TryEstablishAsync(CancellationToken.None);

        Assert.NotEqual(NatStatus.Tunneled, status.NatStatus);
    }

    [Fact]
    public void Priority_IsThree()
    {
        CloudflareTunnelStrategy strategy = BuildStrategy(new());

        Assert.Equal(3, strategy.Priority);
    }

    [Fact]
    public void Type_IsCloudflareTunnel()
    {
        CloudflareTunnelStrategy strategy = BuildStrategy(new());

        Assert.Equal(ConnectivityType.CloudflareTunnel, strategy.Type);
    }

    [Fact]
    public void TeardownAsync_WhenNothingStarted_DoesNotThrow()
    {
        ConnectivityStatus status = new() { CloudflareTunnelToken = null };
        CloudflareTunnelStrategy strategy = BuildStrategy(status);

        Exception? ex = Record.Exception(() => strategy.TeardownAsync().GetAwaiter().GetResult());

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WhenNothingStarted_DoesNotThrow()
    {
        CloudflareTunnelStrategy strategy = BuildStrategy(new());

        Exception? ex = Record.Exception(strategy.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_CalledTwice_OnlyTearsDownOnce_AndDoesNotThrow()
    {
        // The _disposed guard must make the second Dispose() a no-op — this
        // proves the guard exists (a regression here would double-run
        // StopTunnel/dispose the already-disposed Process on the second call).
        CloudflareTunnelStrategy strategy = BuildStrategy(new());

        Exception? ex = Record.Exception(() =>
        {
            strategy.Dispose();
            strategy.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact]
    public async Task TeardownAsync_AfterDispose_DoesNotThrow()
    {
        CloudflareTunnelStrategy strategy = BuildStrategy(new());
        strategy.Dispose();

        Exception? ex = await Record.ExceptionAsync(strategy.TeardownAsync);

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(
        "2026-08-04T19:00:19Z ERR  error=\"Incoming request ended abruptly: context canceled\" connIndex=2 event=1 ingressRule=0 originService=https://127.0.0.1:7626"
    )]
    [InlineData(
        "2026-08-04T19:00:19Z ERR Request failed error=\"Incoming request ended abruptly: context canceled\" connIndex=2 dest=https://server.nomercy.tv/api/v1/home/continue event=0 type=http"
    )]
    [InlineData(
        "2026-08-04T19:01:14Z ERR Request failed error=\"stream 43797 canceled by remote with error code 0\" connIndex=2 dest=https://server.nomercy.tv/video_00015.ts event=0 type=http"
    )]
    public void IsClientCancellation_ClientAbortedRequest_IsNotAFailure(string line)
    {
        Assert.True(CloudflareTunnelStrategy.IsClientCancellation(line));
    }

    [Theory]
    [InlineData(
        "2026-08-04T19:00:19Z ERR Register tunnel error error=\"Unauthorized: Not a valid tunnel token\""
    )]
    [InlineData(
        "2026-08-04T19:00:19Z ERR Request failed error=\"stream 43797 canceled by remote with error code 2\" connIndex=2"
    )]
    [InlineData("2026-08-04T19:00:19Z FTL error=\"failed to dial to edge: dial tcp: i/o timeout\"")]
    public void IsClientCancellation_RealTunnelFailure_StaysAFailure(string line)
    {
        Assert.False(CloudflareTunnelStrategy.IsClientCancellation(line));
    }

    [Fact]
    public async Task TryEstablishAsync_CheckAvailabilityThrows_PropagatesBeforeTokenCheck()
    {
        // The availability callback runs unconditionally before the token
        // gate — if it throws, TryEstablishAsync must not swallow it (no
        // try/catch wraps that call), matching the paid-feature-gate contract
        // the connectivity manager relies on to log the real cause.
        ConnectivityStatus status = new() { CloudflareTunnelToken = "token" };
        CloudflareTunnelStrategy strategy = BuildStrategy(
            status,
            checkAvailability: () => throw new InvalidOperationException("gate check failed")
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            strategy.TryEstablishAsync(CancellationToken.None)
        );
    }

    // ── Shutdown-noise suppression ───────────────────────────────────────────
    //
    // The defect: cloudflared keeps forwarding into a closing origin for a short window
    // around every stop, so "connection refused" and "process exited" logged at Warning on
    // every single shutdown — including ones Stoney triggered on purpose. BeginShutdown()
    // (fired from ApplicationStopping, before Kestrel closes) and StopTunnel() both flip the
    // same flag; LogProcessLine is the one place that reads it.

    [Fact]
    public void LogProcessLine_FailureLine_BeforeShutdown_LogsWarning()
    {
        (CloudflareTunnelStrategy strategy, CapturingLogger logger) = BuildCapturingStrategy(new());

        strategy.LogProcessLine(
            "2026-08-10T20:06:05Z ERR  error=\"Unable to reach the origin service.\" connIndex=2 event=1"
        );

        Assert.Equal([LogLevel.Warning], logger.Levels);
    }

    [Fact]
    public void LogProcessLine_FailureLine_AfterBeginShutdown_LogsDebugNotWarning()
    {
        (CloudflareTunnelStrategy strategy, CapturingLogger logger) = BuildCapturingStrategy(new());

        strategy.BeginShutdown();
        strategy.LogProcessLine(
            "2026-08-10T20:06:05Z ERR  error=\"Unable to reach the origin service.\" connIndex=2 event=1"
        );

        Assert.Equal([LogLevel.Debug], logger.Levels);
    }

    [Fact]
    public void LogProcessLine_ClientCancellationLine_AfterBeginShutdown_StaysDebug()
    {
        // Client cancellations were already Debug before shutdown began; BeginShutdown must
        // not be the only thing keeping them quiet — losing this line would silently widen
        // scope onto behaviour this change never touched.
        (CloudflareTunnelStrategy strategy, CapturingLogger logger) = BuildCapturingStrategy(new());

        strategy.LogProcessLine(
            "2026-08-04T19:00:19Z ERR  error=\"Incoming request ended abruptly: context canceled\" connIndex=2 event=1"
        );

        Assert.Equal([LogLevel.Debug], logger.Levels);
    }

    [Fact]
    public async Task StopTunnel_ViaTeardownAsync_AlsoSetsTheShutdownFlag()
    {
        // BeginShutdown is the early hook (fired before Kestrel stops); TeardownAsync/
        // StopTunnel is the direct path when nothing wired the host lifecycle event. Both
        // must gate the same way, or a caller that only ever reaches TeardownAsync keeps
        // getting warnings.
        (CloudflareTunnelStrategy strategy, CapturingLogger logger) = BuildCapturingStrategy(new());

        await strategy.TeardownAsync();
        strategy.LogProcessLine(
            "2026-08-10T20:06:05Z ERR  error=\"Unable to reach the origin service.\" connIndex=2 event=1"
        );

        Assert.Equal([LogLevel.Debug], logger.Levels);
    }

    [Fact]
    public async Task TryEstablishAsync_AfterAFailedAttempt_TheNextAttemptLogsFailuresAtWarningAgain()
    {
        // The defect (#224): every failed attempt ends in StopTunnel, which raises the
        // intentional-stop flag, and nothing lowered it again. The next attempt on the same
        // instance then ran with the flag still up, so a genuine cloudflared error or exit
        // during it was logged at Debug — invisible in the very log a user reads to find out
        // why their tunnel keeps dropping.
        //
        // The first attempt fails for real: the isolated test app-data root never holds a
        // cloudflared binary, so Process.Start throws and the attempt ends in StopTunnel. The
        // second attempt stops at the binary check, which leaves whatever the attempt start
        // did to the flag observable through LogProcessLine, the same gate the process
        // event handlers read.
        CapturingLogger logger = new();
        int binaryChecks = 0;
        CloudflareTunnelStrategy strategy = new(
            logger,
            new ConnectivityStatus { CloudflareTunnelToken = "assigned-token" },
            binaryExists: () => binaryChecks++ == 0
        );

        ConnectivityResult first = await strategy.TryEstablishAsync(CancellationToken.None);
        Assert.False(first.Established);

        ConnectivityResult second = await strategy.TryEstablishAsync(CancellationToken.None);
        Assert.False(second.Established);

        logger.Levels.Clear();
        strategy.LogProcessLine(
            "2026-08-10T20:06:05Z ERR Register tunnel error error=\"Unauthorized: Not a valid tunnel token\""
        );

        Assert.Equal([LogLevel.Warning], logger.Levels);
    }

    // ── Self-heal on a stuck-looping post-registration rejection ────────────────────────

    // A real process is attached so IsStillEstablished reflects an actual kill, not a
    // trivially-false null process.
    [Fact]
    public void Watch_RegisteredThenStuckUnauthorized_SelfStopsSoNoLongerEstablished()
    {
        using Process process = StartLongRunningProcess();
        CloudflareTunnelStrategy strategy = BuildStrategy(new());
        strategy.AttachProcessForTests(process);

        Assert.True(strategy.IsStillEstablished);

        strategy.Watch(
            "2026-11-05T02:00:00Z INF Registered tunnel connection connIndex=0 connection=e051694c location=ams17 protocol=http2"
        );
        for (int i = 0; i < 3; i++)
            strategy.Watch(
                "2026-11-05T02:01:00Z ERR Register tunnel error from server side error=\"Unauthorized: Tunnel not found\" connIndex=0"
            );

        Assert.False(strategy.IsStillEstablished);
    }

    [Fact]
    public void Watch_TransientErrorLineAfterRegistration_DoesNotSelfStop()
    {
        using Process process = StartLongRunningProcess();
        try
        {
            CloudflareTunnelStrategy strategy = BuildStrategy(new());
            strategy.AttachProcessForTests(process);

            strategy.Watch(
                "2026-11-05T02:00:00Z INF Registered tunnel connection connIndex=0 connection=e051694c location=ams17 protocol=http2"
            );
            strategy.Watch(
                "2026-11-05T02:00:05Z ERR error=\"Unable to reach the origin service.\" connIndex=0"
            );

            Assert.True(strategy.IsStillEstablished);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(true);
        }
    }
}
