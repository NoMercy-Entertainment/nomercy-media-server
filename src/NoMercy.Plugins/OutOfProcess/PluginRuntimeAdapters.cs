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
/// The three folders a plugin owns, and the owner's folders it was granted.
/// <para>
/// The owned three need no grant: they are inside the plugin's own data
/// folder, which the server created for it. Everything else is a grant lookup,
/// and a folder with no grant resolves to nothing rather than to a path the
/// plugin then fails to open.
/// </para>
/// </summary>
public sealed class PluginStorageRoots(Ulid pluginId, string dataFolder, IPluginGrantStore grants)
    : IPluginStorageRoots
{
    public string PrivateRoot { get; } = dataFolder;

    public string TempRoot { get; } = Path.Combine(dataFolder, "tmp");

    public string DerivedRoot { get; } = Path.Combine(dataFolder, "derived");

    public Task<string?> PathForAsync(string folderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            return Task.FromResult<string?>(null);
        }

        // The granted value is the path. Matching on the identifier the plugin
        // sent rather than searching the disk: a plugin that names a folder it
        // was never granted must get nothing, not the nearest match.
        string? granted = grants
            .Granted(pluginId, PluginGrantKind.ForCapability(PluginCapabilityNames.StoragePath))
            .FirstOrDefault(value =>
                string.Equals(value, folderId, StringComparison.OrdinalIgnoreCase)
            );

        return Task.FromResult<string?>(granted);
    }
}

/// <summary>Builds one plugin's roots once its data folder is known.</summary>
public sealed class PluginStorageRootsFactory(IPluginGrantStore grants) : IPluginStorageRootsFactory
{
    public IPluginStorageRoots For(Ulid pluginId, string dataFolder) =>
        new PluginStorageRoots(pluginId, dataFolder, grants);
}

/// <summary>
/// Whether a plugin holds a capability at all, whatever its scope.
/// <para>
/// The sandbox profile is written from this: it needs to know if the plugin
/// may start anything, not which binaries it may start.
/// </para>
/// </summary>
public sealed class PluginCapabilityGrants(IPluginGrantStore grants) : IPluginCapabilityGrants
{
    public bool Holds(Ulid pluginId, string capability) =>
        grants.Granted(pluginId, capability).Count > 0;
}
