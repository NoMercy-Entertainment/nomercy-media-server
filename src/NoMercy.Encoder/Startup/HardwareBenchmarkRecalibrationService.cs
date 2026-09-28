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
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;

namespace NoMercy.Encoder.Startup;

/// <summary>
/// Periodically checks whether the hardware speed index needs recalibration
/// and kicks off <see cref="IHardwareBenchmark.CalibrateAsync"/> when the age
/// threshold (30 days) or a driver-change signal fires.
///
/// Runs independently of <see cref="HardwareBenchmarkBackgroundService"/> which
/// handles the first-boot / driver-change-on-boot case. This service handles
/// the ongoing scheduled recalibration while the server is running.
///
/// Idle gate: if the encoder is busy when recalibration is due, the service
/// defers in 5-minute increments. After 7 days of continuous busy state it
/// emits a warning and skips — the next hour-tick will evaluate again.
/// </summary>
public sealed class HardwareBenchmarkRecalibrationService : BackgroundService
{
    internal static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan DefaultBusyRetryInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DefaultMaxDeferralWindow = TimeSpan.FromDays(7);
    internal static readonly TimeSpan RecalibrationAge = TimeSpan.FromDays(30);

    private readonly IHardwareBenchmark _benchmark;
    private readonly IDriverChangeDetector _driverChangeDetector;
    private readonly ISpeedIndexStore _store;
    private readonly IEncoderActivityProbe _activityProbe;
    private readonly EncoderOptions _options;
    private readonly ILogger<HardwareBenchmarkRecalibrationService> _logger;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _busyRetryInterval;
    private readonly TimeSpan _maxDeferralWindow;

    public HardwareBenchmarkRecalibrationService(
        IHardwareBenchmark benchmark,
        IDriverChangeDetector driverChangeDetector,
        ISpeedIndexStore store,
        IEncoderActivityProbe activityProbe,
        EncoderOptions options,
        ILogger<HardwareBenchmarkRecalibrationService> logger
    )
        : this(
            benchmark,
            driverChangeDetector,
            store,
            activityProbe,
            options,
            logger,
            DefaultCheckInterval,
            DefaultBusyRetryInterval,
            DefaultMaxDeferralWindow
        ) { }

    internal HardwareBenchmarkRecalibrationService(
        IHardwareBenchmark benchmark,
        IDriverChangeDetector driverChangeDetector,
        ISpeedIndexStore store,
        IEncoderActivityProbe activityProbe,
        EncoderOptions options,
        ILogger<HardwareBenchmarkRecalibrationService> logger,
        TimeSpan checkInterval,
        TimeSpan busyRetryInterval,
        TimeSpan maxDeferralWindow
    )
    {
        _benchmark = benchmark;
        _driverChangeDetector = driverChangeDetector;
        _store = store;
        _activityProbe = activityProbe;
        _options = options;
        _logger = logger;
        _checkInterval = checkInterval;
        _busyRetryInterval = busyRetryInterval;
        _maxDeferralWindow = maxDeferralWindow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoCalibrate)
        {
            _logger.LogDebug("AutoCalibrate disabled — periodic recalibration service is dormant");
            return;
        }

        using PeriodicTimer timer = new(_checkInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await EvaluateAndRecalibrateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // An unhandled throw here would fault the BackgroundService and trip
                // the StopHost default — a single bad hourly tick must not take the
                // server down. Log and retry on the next interval (as the sibling
                // periodic services already do).
                _logger.LogError(
                    ex,
                    "Hardware recalibration tick failed; retrying on the next interval"
                );
            }
        }
    }

    internal async Task EvaluateAndRecalibrateAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return;

        bool driverChanged = await DetectDriverChangeAsync(ct).ConfigureAwait(false);
        bool ageExceeded = IsAgeExceeded();

        if (!driverChanged && !ageExceeded)
        {
            _logger.LogDebug(
                "Recalibration check: speed index is fresh and driver unchanged — nothing to do"
            );
            return;
        }

        string reason = driverChanged
            ? "driver change detected"
            : "speed index age exceeded 30 days";
        _logger.LogInformation("Recalibration triggered: {Reason}", reason);

        bool triggered = await WaitForIdleAndRunAsync(reason, ct).ConfigureAwait(false);

        if (!triggered)
        {
            _logger.LogWarning(
                "Recalibration skipped after {MaxDeferral} of continuous busy state — will re-evaluate next hour",
                _maxDeferralWindow
            );
        }
    }

    private async Task<bool> DetectDriverChangeAsync(CancellationToken ct)
    {
        try
        {
            DriverChangeResult result = await _driverChangeDetector
                .DetectAndPersistAsync(ct)
                .ConfigureAwait(false);
            return result.Changed;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Driver fingerprint check failed — treating as unchanged");
            return false;
        }
    }

    private bool IsAgeExceeded()
    {
        DateTime? lastCalibrated = _store.LastCalibratedAt;
        if (lastCalibrated is null)
            return true;

        return DateTime.UtcNow - lastCalibrated.Value > RecalibrationAge;
    }

    private async Task<bool> WaitForIdleAndRunAsync(string reason, CancellationToken ct)
    {
        DateTime deferralStart = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            if (!_activityProbe.IsBusy)
                break;

            TimeSpan elapsed = DateTime.UtcNow - deferralStart;
            if (elapsed >= _maxDeferralWindow)
                return false;

            _logger.LogDebug(
                "Recalibration deferred — encoder busy (elapsed: {Elapsed:g}), retry in {Retry}", [elapsed, _busyRetryInterval]
            );

            try
            {
                await Task.Delay(_busyRetryInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        if (ct.IsCancellationRequested)
            return false;

        try
        {
            _logger.LogInformation("Starting hardware recalibration ({Reason})", reason);
            SpeedIndex result = await _benchmark.CalibrateAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Hardware recalibration finished — {Count} measurements captured",
                result.Measurements.Count
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Hardware recalibration cancelled");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hardware recalibration failed");
            return false;
        }
    }
}
