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
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace NoMercy.Plugins;

internal sealed class PluginRegistry : IPluginRegistry
{
    private readonly ConcurrentDictionary<Ulid, LoadedPlugin> _plugins = new();

    /// <summary>
    /// Replaces the entry for <paramref name="id"/>. Every call site that
    /// re-registers an id (a manifest reload, a direct-assembly reload, a
    /// malfunctioned-plugin re-record) used to overwrite the previous
    /// <see cref="LoadedPlugin"/> outright — its <c>Instance</c> was never
    /// disposed and its <c>LoadContext</c> was never unloaded, leaking a
    /// collectible ALC that pins the old assembly file (surfaces later as an
    /// IOException when uninstall tries to delete the plugin directory).
    /// Centralizing the dispose+unload here fixes every caller in one place.
    /// </summary>
    public LoadedPlugin this[Ulid id]
    {
        set
        {
            LoadedPlugin? replaced = null;
            _plugins.AddOrUpdate(
                id,
                value,
                (_, existing) =>
                {
                    replaced = existing;
                    return value;
                }
            );

            if (replaced is not null && !ReferenceEquals(replaced, value))
            {
                replaced.Instance?.Dispose();
                replaced.LoadContext?.Unload();
                PluginShadowCopy.TryDelete(replaced.ShadowDirectory);
            }
        }
    }

    public bool TryGetValue(Ulid id, [MaybeNullWhen(false)] out LoadedPlugin plugin)
    {
        return _plugins.TryGetValue(id, out plugin);
    }

    public bool TryRemove(Ulid id, [MaybeNullWhen(false)] out LoadedPlugin plugin)
    {
        return _plugins.TryRemove(id, out plugin);
    }

    public ICollection<LoadedPlugin> Values => _plugins.Values;

    public void Clear()
    {
        _plugins.Clear();
    }
}
