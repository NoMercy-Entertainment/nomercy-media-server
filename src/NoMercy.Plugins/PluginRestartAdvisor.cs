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

namespace NoMercy.PluginSdk;

/// <summary>
/// Answers whether an action on a plugin needs the server restarted.
/// <para>
/// Mostly it does not, and that is the answer worth giving. Nothing said either
/// way before, so every plugin action looked like it might need a restart and
/// an owner learns to restart after all of them — which is the worst outcome
/// for a self-hosted server people leave running.
/// </para>
/// </summary>
public interface IPluginRestartAdvisor
{
    PluginRestartRequirement Evaluate(PluginInfo plugin, PluginOperation operation);

    /// <summary>
    /// Records that a plugin's services were registered during the pre-build
    /// pass, which is the only time they can be.
    /// </summary>
    void MarkRegisteredAtStartup(Ulid pluginId);
}

public class PluginRestartAdvisor(IPluginAssemblyTracker? tracker = null) : IPluginRestartAdvisor
{
    private readonly HashSet<Ulid> _registeredAtStartup = [];
    private readonly Lock _gate = new();

    public void MarkRegisteredAtStartup(Ulid pluginId)
    {
        lock (_gate)
            _registeredAtStartup.Add(pluginId);
    }

    public PluginRestartRequirement Evaluate(PluginInfo plugin, PluginOperation operation)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        PluginRestartReason reasons = PluginRestartReason.None;

        switch (operation)
        {
            case PluginOperation.Enable:
            case PluginOperation.Install:
                // Services and routes are both collected before the container
                // and the pipeline are built. A plugin that was already present
                // at startup had its turn; one arriving now has missed it.
                if (plugin.ContributesServices && !WasRegisteredAtStartup(plugin.Id))
                    reasons |= PluginRestartReason.ContributesServices;

                if (plugin.Capabilities?.Rest == true && !WasRegisteredAtStartup(plugin.Id))
                    reasons |= PluginRestartReason.OwnsRoutes;

                break;

            case PluginOperation.Disable:
                // Nothing. Dispose runs, hooks stop being consulted, and the
                // plugin stops acting — none of which needs the process to end.
                // Its services stay registered and unused, which is untidy and
                // not a reason to restart a media server mid-playback.
                break;

            case PluginOperation.Uninstall:
            case PluginOperation.Update:
                // Only when the assembly really is still resident. Unload is
                // best-effort, so this is looked up rather than assumed — a
                // clean uninstall should be told it was clean, and saying
                // "restart required" after every one is how an owner learns to
                // ignore the message.
                if (tracker?.IsStillLoaded(plugin.Id) ?? true)
                    reasons |= PluginRestartReason.AssemblyStillLoaded;

                break;
        }

        return new(reasons);
    }

    private bool WasRegisteredAtStartup(Ulid pluginId)
    {
        lock (_gate)
            return _registeredAtStartup.Contains(pluginId);
    }
}
