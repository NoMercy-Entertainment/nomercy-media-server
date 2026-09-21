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
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Watchdog;

/// <summary>What a plugin is using right now, as far as this process can tell.</summary>
public interface IPluginResourceSampler
{
    PluginResourceSample? Sample(Ulid pluginId);
}

/// <summary>
/// Looks at every running plugin every ten seconds and hands what it sees to
/// the watchdog.
/// <para>
/// Ten seconds because the thing being caught is a plugin eating the server,
/// and a minute of that is a minute the owner spends wondering why nothing
/// responds. It is cheap: reading counters, not walking anything.
/// </para>
/// </summary>
public class PluginWatchdogService(
    IPluginManager plugins,
    IPluginResourceSampler sampler,
    PluginWatchdog watchdog,
    ILogger<PluginWatchdogService> logger
) : BackgroundService
{
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Sweep();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One bad sweep must not take the watchdog down with it: a
                // watchdog that stopped is a server with no ceilings at all,
                // and nothing would say so until something ate it.
                logger.LogWarning(exception, "The plugin watchdog skipped a sweep.");
            }
        }
    }

    private void Sweep()
    {
        foreach (PluginInfo plugin in plugins.GetInstalledPlugins())
        {
            if (plugin.Status != PluginStatus.Active)
                continue;

            if (sampler.Sample(plugin.Id) is { } sample)
                watchdog.Observe(plugin.Id, sample);
        }
    }
}
