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
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// Where a plugin's files are, read from the registry the server already
/// keeps.
/// <para>
/// The launcher must not go looking on disk: the registry is what knows which
/// assembly a plugin was installed from, and a second search would find a
/// stale copy after an update.
/// </para>
/// </summary>
internal sealed class PluginAssemblyLocation(
    IPluginRegistry registry,
    IPluginCapabilityGrants grants,
    string pluginsPath
) : IPluginAssemblyLocation
{
    public PluginFileLocation For(Ulid pluginId)
    {
        string dataFolder = Path.Combine(pluginsPath, "data", pluginId.ToString());

        string assembly = registry.TryGetValue(pluginId, out LoadedPlugin? loaded)
            ? loaded.Info.AssemblyPath ?? string.Empty
            : string.Empty;

        // Whether it may start anything, asked of the grant rather than the
        // manifest. A manifest states an intention; the grant is the
        // permission, and the sandbox profile is written from the permission.
        bool spawns = grants.Holds(pluginId, PluginCapabilityNames.ProcessSpawn);

        return new PluginFileLocation(assembly, dataFolder, spawns);
    }
}

/// <summary>Whether a plugin actually holds a capability right now.</summary>
public interface IPluginCapabilityGrants
{
    bool Holds(Ulid pluginId, string capability);
}
