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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercyQueue.Workers;

namespace NoMercy.PluginSdk.Hooks;

/// <summary>
/// Registers every active <see cref="IScheduledTaskPlugin"/> that declares the
/// <c>scheduledTask</c> capability as a cron executor instance on
/// <see cref="CronWorker"/>.
/// <para>
/// Reached from more than one path: the boot pass, the lifecycle manager, and
/// <see cref="PluginCronSubscriber"/> on every <c>PluginLoadedEvent</c>. A second
/// sighting of the same instance is therefore a no-op, and a new instance for a
/// plugin id that is already registered (an update loaded beside the old copy)
/// replaces the old instance's jobs instead of adding to them.
/// </para>
/// </summary>
public class PluginCronRegistrar(
    IPluginManager pluginManager,
    CronWorker cronWorker,
    ILogger<PluginCronRegistrar>? logger = null
) : IPluginCronRegistrar
{
    private readonly ILogger _log = logger ?? NullLogger<PluginCronRegistrar>.Instance;
    private readonly object _lock = new();

    /// <summary>
    /// What each plugin registered, by instance. Kept here because a plugin's
    /// job list is read from the instance, and the instance may already be gone
    /// when the plugin is disabled or replaced.
    /// </summary>
    private readonly Dictionary<Ulid, RegisteredPlugin> _registered = [];

    private sealed record RegisteredPlugin(IScheduledTaskPlugin Instance, List<string> JobNames);

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

    public void UnregisterPlugin(Ulid pluginId)
    {
        lock (_lock)
        {
            if (_registered.Remove(pluginId, out RegisteredPlugin? registered))
                RemoveJobs(registered.JobNames);

            // Both derived shapes as well, for an executor registered before
            // this registrar tracked what it registered. The names come from
            // the id alone, so removal does not need the instance.
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

    private void RegisterOne(IScheduledTaskPlugin plugin)
    {
        lock (_lock)
        {
            if (_registered.TryGetValue(plugin.Id, out RegisteredPlugin? existing))
            {
                // The running copy is already scheduled: nothing to do.
                if (ReferenceEquals(existing.Instance, plugin))
                    return;

                // An update loaded a new copy. Its jobs replace the old copy's,
                // which also lets go of the old instance.
                RemoveJobs(existing.JobNames);
                _registered.Remove(plugin.Id);
            }

            List<string> jobNames = [];

            try
            {
                RegisterOneOrThrow(plugin, jobNames);
            }
            catch (Exception exception)
            {
                // One plugin that cannot be scheduled must not stop the others
                // being scheduled, and must not take startup down.
                if (
                    !PluginStaleMemberLog.Explain(
                        _log,
                        plugin.Id,
                        exception,
                        "registering its tasks"
                    )
                )
                    _log.LogError(
                        exception,
                        "Plugin {Plugin} threw while its scheduled tasks were registered; it runs none.",
                        plugin.Id
                    );
            }

            // Recorded even after a throw, so the jobs registered before it can
            // still be removed.
            if (jobNames.Count > 0)
                _registered[plugin.Id] = new(plugin, jobNames);
        }
    }

    private void RegisterOneOrThrow(IScheduledTaskPlugin plugin, List<string> jobNames)
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
            Register(new PluginCronExecutor(plugin), jobNames);
            return;
        }

        foreach (PluginScheduledJob job in jobs)
            Register(new PluginCronExecutor(plugin, job), jobNames);
    }

    private void Register(PluginCronExecutor executor, List<string> jobNames)
    {
        cronWorker.RegisterExecutor(executor);
        jobNames.Add(executor.JobName);
    }

    private void RemoveJobs(List<string> jobNames)
    {
        foreach (string jobName in jobNames)
            cronWorker.RemoveExecutor(jobName);
    }
}
