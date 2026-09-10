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

namespace NoMercy.Plugins.Hooks;

public interface IPluginCronRegistrar
{
    void RegisterAll();

    /// <summary>
    /// Registers one plugin's cron executors, the same way <see cref="RegisterAll"/>
    /// does for every plugin found at boot.
    /// <para>
    /// Boot is not the only time a scheduled-task plugin starts existing in the
    /// process: install, restart and update all bring one back with a fresh
    /// instance, and none of them go through the boot path that calls
    /// <see cref="RegisterAll"/>. Without a call scoped to just this plugin,
    /// its jobs stay unregistered until the next full server start — quietly,
    /// since a plugin can be Active and answering its own REST routes with no
    /// cron work happening behind it at all.
    /// </para>
    /// </summary>
    void RegisterPlugin(Ulid pluginId);

    /// <summary>
    /// Stops and releases every executor registered for one plugin.
    /// <para>
    /// The counterpart to registration, and load-bearing: an executor holds the
    /// plugin instance, so leaving one behind keeps the plugin's collectible
    /// load context alive after it is disabled and its files locked on Windows.
    /// A plugin declaring several jobs leaves several.
    /// </para>
    /// </summary>
    void UnregisterPlugin(Ulid pluginId);
}
