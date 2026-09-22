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

namespace NoMercy.PluginSdk.Telemetry;

/// <summary>
/// Sends one report an hour, covering the hour just gone.
/// <para>
/// Hourly because the thing being watched is a plugin misbehaving across many
/// servers at once, and that shows up in a day of hours rather than in any one
/// minute.
/// </para>
/// </summary>
public class PluginTelemetryService(
    PluginTelemetryReporter reporter,
    TimeProvider clock,
    ILogger<PluginTelemetryService> logger
) : BackgroundService
{
    public static TimeSpan Interval { get; } = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset windowStart = clock.GetUtcNow();

        using PeriodicTimer timer = new(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await reporter.SendAsync(windowStart, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "The plugin telemetry window was not reported.");
            }

            windowStart = clock.GetUtcNow();
        }
    }
}
