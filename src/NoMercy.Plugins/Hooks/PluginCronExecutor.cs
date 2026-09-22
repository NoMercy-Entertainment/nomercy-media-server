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

using NoMercy.PluginSdk.Abstractions;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.PluginSdk.Hooks;

/// <summary>
/// Adapts an <see cref="IScheduledTaskPlugin"/> to <see cref="ICronJobExecutor"/>
/// so <c>CronWorker.RegisterExecutor</c> can schedule it directly as a runtime
/// instance — plugins have no DI registration for the type-based cron path.
/// </summary>
/// <param name="job">
/// One named job from <see cref="IScheduledTaskPlugin.Jobs"/>, or null for the
/// plugin's single <see cref="IScheduledTaskPlugin.CronExpression"/>.
/// </param>
public class PluginCronExecutor(IScheduledTaskPlugin plugin, PluginScheduledJob? job = null)
    : ICronJobExecutor
{
    public string JobName => job is null ? $"plugin:{plugin.Id}" : $"plugin:{plugin.Id}:{job.Name}";

    public string CronExpression => job?.CronExpression ?? plugin.CronExpression;

    /// <summary>
    /// Skips a tick that arrives while the previous one is still running,
    /// unless the job asked for overlap.
    /// <para>A cycle that takes longer than its interval would otherwise pile
    /// up, and the plugin author who declared a one-minute cadence for cheap
    /// work does not expect the expensive job beside it to run twice at once.</para>
    /// </summary>
    private int _running;

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        bool allowConcurrent = job?.AllowConcurrent ?? false;

        if (!allowConcurrent && Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        try
        {
            if (job is null)
                await plugin.ExecuteAsync(cancellationToken);
            else
                await plugin.ExecuteAsync(job.Name, cancellationToken);
        }
        finally
        {
            if (!allowConcurrent)
                Interlocked.Exchange(ref _running, 0);
        }
    }
}
