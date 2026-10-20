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
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Startup;

namespace NoMercy.Tests.Encoder.Startup;

public class HardwareBenchmarkRecalibrationServiceTests
{
    private static readonly DriverChangeResult DriverChanged = new(
        CurrentHash: "new-hash",
        PreviousHash: "old-hash",
        Changed: true,
        IsFirstBoot: false
    );

    private static readonly DriverChangeResult DriverUnchanged = new(
        CurrentHash: "new-hash",
        PreviousHash: "new-hash",
        Changed: false,
        IsFirstBoot: false
    );

    // ──────────────────────────────────────────────────────────────────────────
    // RecalAge30DaysTriggersRecal_WhenIdle
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecalAge30DaysTriggersRecal_WhenIdle()
    {
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-31));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DriverChangeResult(
                    CurrentHash: "abc",
                    PreviousHash: "abc",
                    Changed: false,
                    IsFirstBoot: false
                )
            );

        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.Setup(p => p.IsBusy).Returns(false);

        Mock<IHardwareBenchmark> benchmark = new();
        benchmark
            .Setup(b => b.CalibrateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpeedIndex(new()));

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);

        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RecalSkippedWhenBusy
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecalSkippedWhenBusy()
    {
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-31));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DriverChangeResult(
                    CurrentHash: "abc",
                    PreviousHash: "abc",
                    Changed: false,
                    IsFirstBoot: false
                )
            );

        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.Setup(p => p.IsBusy).Returns(true);

        Mock<IHardwareBenchmark> benchmark = new();

        // Use a zero max-deferral window so the test doesn't wait 7 days.
        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object,
            maxDeferralWindow: TimeSpan.Zero
        );

        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);

        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RecalSkippedWhenFresh
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecalSkippedWhenFresh()
    {
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-5));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DriverChangeResult(
                    CurrentHash: "same",
                    PreviousHash: "same",
                    Changed: false,
                    IsFirstBoot: false
                )
            );

        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.Setup(p => p.IsBusy).Returns(false);

        Mock<IHardwareBenchmark> benchmark = new();

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);

        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // DriverChangeTriggersRecal
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DriverChangeTriggersRecal()
    {
        Mock<ISpeedIndexStore> store = new();
        // Fresh timestamp — age alone would NOT trigger recal.
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-1));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .Setup(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DriverChangeResult(
                    CurrentHash: "new-hash",
                    PreviousHash: "old-hash",
                    Changed: true,
                    IsFirstBoot: false
                )
            );

        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.Setup(p => p.IsBusy).Returns(false);

        Mock<IHardwareBenchmark> benchmark = new();
        benchmark
            .Setup(b => b.CalibrateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpeedIndex(new()));

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);

        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Driver change keeps triggering until a run succeeds
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DriverChangeRetriedNextTick_WhenRecalFails()
    {
        Mock<ISpeedIndexStore> store = new();
        // Fresh timestamp — only the driver change can trigger recal.
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-1));

        // The detector persists the new hash on the first tick, so it reports
        // the change exactly once.
        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .SetupSequence(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DriverChanged)
            .ReturnsAsync(DriverUnchanged);

        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.Setup(p => p.IsBusy).Returns(false);

        Mock<IHardwareBenchmark> benchmark = new();
        benchmark
            .SetupSequence(b => b.CalibrateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("benchmark exploded"))
            .ReturnsAsync(new SpeedIndex(new()));

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);
        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);

        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DriverChangeRetriedNextTick_WhenRecalSkippedBusy()
    {
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-1));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .SetupSequence(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DriverChanged)
            .ReturnsAsync(DriverUnchanged);

        // Busy through the whole (zero-length) deferral window on the first
        // tick, idle on the second.
        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.SetupSequence(p => p.IsBusy).Returns(true).Returns(false);

        Mock<IHardwareBenchmark> benchmark = new();
        benchmark
            .Setup(b => b.CalibrateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpeedIndex(new()));

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object,
            maxDeferralWindow: TimeSpan.Zero
        );

        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);
        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Never);

        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);
        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DriverChangeStopsTriggering_AfterRecalSucceeds()
    {
        Mock<ISpeedIndexStore> store = new();
        store.Setup(s => s.LastCalibratedAt).Returns(DateTime.UtcNow.AddDays(-1));

        Mock<IDriverChangeDetector> driverDetector = new();
        driverDetector
            .SetupSequence(d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DriverChanged)
            .ReturnsAsync(DriverUnchanged)
            .ReturnsAsync(DriverUnchanged);

        Mock<IEncoderActivityProbe> activityProbe = new();
        activityProbe.Setup(p => p.IsBusy).Returns(false);

        Mock<IHardwareBenchmark> benchmark = new();
        benchmark
            .SetupSequence(b => b.CalibrateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("benchmark exploded"))
            .ReturnsAsync(new SpeedIndex(new()));

        HardwareBenchmarkRecalibrationService sut = BuildService(
            benchmark.Object,
            driverDetector.Object,
            store.Object,
            activityProbe.Object
        );

        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);
        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);
        await sut.EvaluateAndRecalibrateAsync(CancellationToken.None);

        // Failed once, succeeded once, then nothing left to do: the hash was
        // persisted by the detector and the pending change is cleared.
        benchmark.Verify(b => b.CalibrateAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        driverDetector.Verify(
            d => d.DetectAndPersistAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(3)
        );
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static HardwareBenchmarkRecalibrationService BuildService(
        IHardwareBenchmark benchmark,
        IDriverChangeDetector driverDetector,
        ISpeedIndexStore store,
        IEncoderActivityProbe activityProbe,
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
            busyRetryInterval: TimeSpan.FromMilliseconds(1),
            maxDeferralWindow: maxDeferralWindow
                ?? HardwareBenchmarkRecalibrationService.DefaultMaxDeferralWindow
        );
}
