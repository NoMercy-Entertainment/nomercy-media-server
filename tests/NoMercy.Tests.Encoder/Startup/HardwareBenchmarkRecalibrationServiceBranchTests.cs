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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Startup;

namespace NoMercy.Tests.Encoder.Startup;

/// <summary>
/// Branch-coverage gaps not reached by
/// <see cref="HardwareBenchmarkRecalibrationServiceTests"/>:
///
/// • AutoCalibrate=false — ExecuteAsync returns immediately, no timer set up.
/// • LastCalibratedAt=null — first-ever boot path; IsAgeExceeded returns true
///   so recalibration triggers without a known prior timestamp.
/// • DriverChangeDetector throws — error is swallowed, treated as "no change".
///   The recalibrator must NEVER crash from a faulty driver-fingerprint backend.
/// • CalibrateAsync throws — exception logged, returned false from
///   WaitForIdleAndRunAsync, no propagation to caller.
/// • CalibrateAsync OperationCanceledException — surfaced as "cancelled" log
///   and false return, NOT propagated as an exception.
/// • WaitForIdleAndRunAsync cancellation during deferral Task.Delay returns
///   false without throwing.
/// • Cancellation token pre-checked at EvaluateAndRecalibrateAsync entry.
/// </summary>
public class HardwareBenchmarkRecalibrationServiceBranchTests
{
    // ── AutoCalibrate=false ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_returns_immediately_when_AutoCalibrate_is_false()
    {
        // BackgroundService surface — ExecuteAsync is protected. We test the
        // dormant contract: when AutoCalibrate=false, the service must NOT
        // hit DetectAndPersistAsync (which it would on the first timer tick).
        Mock<IDriverChangeDetector> driverDetector = new();
        Mock<IHardwareBenchmark> benchmark = new();
        Mock<ISpeedIndexStore> store = new();
        Mock<IEncoderActivityProbe> activityProbe = new();

        HardwareBenchmarkRecalibrationService sut = new(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object,
            new() { AutoCalibrate = false },
            NullLogger<HardwareBenchmarkRecalibrationService>.Instance,
            checkInterval: TimeSpan.FromMilliseconds(1),
            busyRetryInterval: TimeSpan.FromMilliseconds(1),
            maxDeferralWindow: TimeSpan.Zero
        );

        // Drive ExecuteAsync via StartAsync + StopAsync. The CT we pass to
        // StopAsync is independent of the internal stoppingToken — we just
        // want to confirm nothing happened.
        await ((IHostedService)sut).StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await ((IHostedService)sut).StopAsync(CancellationToken.None);

        driverDetector.Verify(
            d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()),
            Times.Never
        );
        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── First-boot — LastCalibratedAt is null ────────────────────────────────

    [Fact]
    public async Task LastCalibratedAt_null_triggers_recalibration_as_if_age_exceeded()
    {
        // First-ever boot: store has no prior timestamp. The "fresh" check
        // must NOT silently no-op — IsAgeExceeded returns true so recal fires.
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns((DateTime?)null);

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DriverChangeResult(
                    CurrentHash: "x",
                    PreviousHash: "x",
                    Changed: false,
                    IsFirstBoot: false
                )
            );

        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.Setup(p => p.IsBusy).Returns(false);

        Mock<IHardwareBenchmark> benchmark = new();
        benchmark
            .Setup(b => b.CalibrateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpeedIndex([]));

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);

        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── DriverChangeDetector throws → treated as unchanged ───────────────────

    [Fact]
    public async Task DriverChangeDetector_throws_is_swallowed_and_treated_as_unchanged()
    {
        // The fingerprint backend can fail (WMI hiccup, sysfs permission error)
        // — must NOT crash the recalibration service. Combined with a fresh
        // store, the failure should result in NO recalibration call.
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-1)); // fresh

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("driver fingerprint failed"));

        Mock<IEncoderActivityProbe> activityProbe = new();
        Mock<IHardwareBenchmark> benchmark = new();

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        // Must not throw, and must not call CalibrateAsync since store is fresh + driver "unchanged".
        Func<Task> act = () => sut.EvaluateAndRecalibrateAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DriverChangeDetector_timeout_does_not_propagate()
    {
        // An uncancelled token means this exception is a timeout, not shutdown.
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-1));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        Mock<IHardwareBenchmark> benchmark = new();
        Mock<IEncoderActivityProbe> activityProbe = new();

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        Func<Task> act = () => sut.EvaluateAndRecalibrateAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DriverChangeDetector_shutdown_cancellation_propagates_out()
    {
        using CancellationTokenSource stopping = new();
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-1));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .Callback(() => stopping.Cancel())
            .ThrowsAsync(new OperationCanceledException());

        HardwareBenchmarkRecalibrationService sut = BuildService(
            new Mock<IHardwareBenchmark>().Object,
            driverDetector.Object,
            store.Object,
            new Mock<IEncoderActivityProbe>().Object
        );

        Func<Task> act = () => sut.EvaluateAndRecalibrateAsync(stopping.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── CalibrateAsync throws ────────────────────────────────────────────────

    [Fact]
    public async Task CalibrateAsync_throws_is_logged_and_does_not_propagate()
    {
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-31));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DriverChangeResult(
                    CurrentHash: "x",
                    PreviousHash: "x",
                    Changed: false,
                    IsFirstBoot: false
                )
            );

        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.Setup(p => p.IsBusy).Returns(false);

        Mock<IHardwareBenchmark> benchmark = new();
        benchmark
            .Setup(b => b.CalibrateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("benchmark exploded"));

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        Func<Task> act = () => sut.EvaluateAndRecalibrateAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CalibrateAsync_OperationCanceledException_does_not_propagate()
    {
        // Different from a general exception — the OperationCanceledException
        // arm explicitly logs "cancelled" and returns false.
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-31));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DriverChangeResult(
                    CurrentHash: "x",
                    PreviousHash: "x",
                    Changed: false,
                    IsFirstBoot: false
                )
            );

        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.Setup(p => p.IsBusy).Returns(false);

        Mock<IHardwareBenchmark> benchmark = new();
        benchmark
            .Setup(b => b.CalibrateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        Func<Task> act = () => sut.EvaluateAndRecalibrateAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── WaitForIdleAndRunAsync cancellation during deferral ──────────────────

    [Fact]
    public async Task Idle_wait_cancellation_during_deferral_returns_without_calibrating()
    {
        // Busy encoder + non-zero max-deferral window. Cancellation during
        // Task.Delay must return cleanly without calling CalibrateAsync.
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-31));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DriverChangeResult(
                    CurrentHash: "x",
                    PreviousHash: "x",
                    Changed: false,
                    IsFirstBoot: false
                )
            );

        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.Setup(p => p.IsBusy).Returns(true); // always busy

        Mock<IHardwareBenchmark> benchmark = new();

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object,
            busyRetryInterval: TimeSpan.FromSeconds(10), // long retry to force delay
            maxDeferralWindow: TimeSpan.FromMinutes(10) // doesn't elapse
        );

        CancellationTokenSource cts = new();
        Task evaluateTask = sut.EvaluateAndRecalibrateAsync(cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        Func<Task> act = () => evaluateTask;
        await act.Should().NotThrowAsync();
        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Cancellation pre-check ───────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAndRecalibrateAsync_with_pre_cancelled_token_returns_immediately()
    {
        // Top-of-method `if (ct.IsCancellationRequested) return;` short-circuit.
        Mock<ISpeedIndexStore> store = new();
        Mock<IDriverChangeDetector> driverDetector = new();
        Mock<IEncoderActivityProbe> activityProbe = new();
        Mock<IHardwareBenchmark> benchmark = new();

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await sut.EvaluateAndRecalibrateAsync(cts.Token);

        // Pre-cancelled — nothing should have been called.
        driverDetector.Verify(
            d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()),
            Times.Never
        );
        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HardwareBenchmarkRecalibrationService BuildService(
        IHardwareBenchmark benchmark,
        IDriverChangeDetector driverDetector,
        ISpeedIndexStore store,
        IEncoderActivityProbe activityProbe,
        TimeSpan? busyRetryInterval = null,
        TimeSpan? maxDeferralWindow = null
    ) =>
        new(
            benchmark,
            driverDetector,
            store,
            activityProbe,
            new() { AutoCalibrate = true },
            NullLogger<HardwareBenchmarkRecalibrationService>.Instance,
            checkInterval: TimeSpan.FromMilliseconds(1),
            busyRetryInterval: busyRetryInterval ?? TimeSpan.FromMilliseconds(1),
            maxDeferralWindow: maxDeferralWindow
                ?? HardwareBenchmarkRecalibrationService.DefaultMaxDeferralWindow
        );
}
