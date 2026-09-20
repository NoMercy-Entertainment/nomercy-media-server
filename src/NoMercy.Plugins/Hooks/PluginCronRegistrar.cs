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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercyQueue.Workers;

namespace NoMercy.Plugins.Hooks;

/// <summary>
/// Registers every active <see cref="IScheduledTaskPlugin"/> that declares the
/// <c>scheduledTask</c> capability as a cron executor instance on
/// <see cref="CronWorker"/>. Called once, post plugin-load.
/// </summary>
public class PluginCronRegistrar(
    IPluginManager pluginManager,
    CronWorker cronWorker,
    ILogger<PluginCronRegistrar>? logger = null
) : IPluginCronRegistrar
{
    private readonly ILogger _log = logger ?? NullLogger<PluginCronRegistrar>.Instance;

    public void RegisterAll()
    {
        foreach (
            IScheduledTaskPlugin plugin in pluginManager.GetPluginsOfType<IScheduledTaskPlugin>()
        )
            RegisterOne(plugin);
    }

    public void RegisterPlugin(Ulid pluginId)
    {
        foreach (
            IScheduledTaskPlugin plugin in pluginManager.GetPluginsOfType<IScheduledTaskPlugin>()
        )
        {
            if (plugin.Id != pluginId)
                continue;

            RegisterOne(plugin);
            return;
        }
    }

    private void RegisterOne(IScheduledTaskPlugin plugin)
    {
        try
        {
            RegisterOneOrThrow(plugin);
        }
        catch (Exception exception)
        {
            // One plugin that cannot be scheduled must not stop the others
            // being scheduled, and must not take startup down.
            if (PluginStaleMemberLog.Explain(_log, plugin.Id, exception, "registering its tasks"))
                return;

            _log.LogError(
                exception,
                "Plugin {Plugin} threw while its scheduled tasks were registered; it runs none.",
                plugin.Id
            );
        }
    }

    private void RegisterOneOrThrow(IScheduledTaskPlugin plugin)
    {
        PluginCapabilities? capabilities = pluginManager.GetPluginInfo(plugin.Id)?.Capabilities;

        if (!PluginCapabilityGuard.DeclaresHook(capabilities, PluginHookCapability.ScheduledTask))
            return;

        // Each declared job on its own schedule, so the server's job list
        // shows the real work instead of one opaque entry, and an expensive
        // cycle can be timed and disabled apart from a cheap one. A plugin
        // that declares none keeps its single expression exactly as before.
        IReadOnlyList<PluginScheduledJob> jobs = plugin.Jobs;

        if (jobs.Count == 0)
        {
            cronWorker.RegisterExecutor(new PluginCronExecutor(plugin));
            return;
        }

        foreach (PluginScheduledJob job in jobs)
            cronWorker.RegisterExecutor(new PluginCronExecutor(plugin, job));
    }

    public void UnregisterPlugin(Ulid pluginId)
    {
        // Both shapes, because a plugin's job list is read from the instance
        // and the instance may already be gone by the time it is disabled. The
        // names are derivable from the id alone, so removal does not depend on
        // the plugin still being there to ask.
        cronWorker.RemoveExecutor($"plugin:{pluginId}");

        foreach (
            IScheduledTaskPlugin plugin in pluginManager.GetPluginsOfType<IScheduledTaskPlugin>()
        )
        {
            if (plugin.Id != pluginId)
                continue;

            foreach (PluginScheduledJob job in plugin.Jobs)
                cronWorker.RemoveExecutor($"plugin:{pluginId}:{job.Name}");
        }
    }
}
