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
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Watchdog;

/// <summary>
/// What the watchdog's three verbs mean on this host.
/// <para>
/// Narrow on purpose: the thing that decides a plugin is misbehaving reaches
/// restart and disable and nothing else. It cannot install, uninstall or read
/// what a plugin stored.
/// </para>
/// </summary>
public class PluginManagerWatchdogLifecycle(
    Func<IPluginManager?> plugins,
    ILogger<PluginManagerWatchdogLifecycle> logger
) : IPluginWatchdogLifecycle
{
    /// <summary>
    /// Nothing yet, and it says so rather than pretending. A plugin runs on
    /// the server's own threads, so slowing one down means slowing down
    /// whatever it is running inside, which is the server. Stage two runs a
    /// plugin somewhere that can actually be throttled.
    /// </summary>
    public void Throttle(Ulid pluginId) =>
        logger.LogWarning(
            "Plugin {PluginId} is over its processor allowance. This server cannot slow one plugin down on its own, so it is being left running and reported.",
            pluginId
        );

    public void Restart(Ulid pluginId) => Fire(pluginId, "restart");

    public void Disable(Ulid pluginId) => Fire(pluginId, "disable");

    private void Fire(Ulid pluginId, string what)
    {
        IPluginManager? manager = plugins();

        if (manager is null)
            return;

        // Not awaited: the watchdog sweeps on a timer and a restart that takes
        // its time must not hold up looking at every other plugin. Failures
        // are logged where they happen rather than swallowed here.
        _ = Task.Run(async () =>
        {
            try
            {
                if (what == "restart")
                    await manager.RestartPluginAsync(pluginId);
                else
                    await manager.DisablePluginAsync(pluginId);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "The watchdog could not {What} plugin {PluginId}.",
                    what,
                    pluginId
                );
            }
        });
    }
}
