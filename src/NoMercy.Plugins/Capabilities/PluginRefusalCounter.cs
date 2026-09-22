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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Capabilities;

/// <summary>
/// In memory, per process. A count that survived a restart would be a count
/// nobody can clear by fixing the plugin.
/// </summary>
public sealed class PluginRefusalCounter : IPluginRefusalCounter
{
    private readonly ConcurrentDictionary<Ulid, ConcurrentDictionary<string, int>> _counts = new();

    public void Count(Ulid pluginId, PluginRefusal refusal)
    {
        ConcurrentDictionary<string, int> forPlugin = _counts.GetOrAdd(
            pluginId,
            static _ => new(StringComparer.Ordinal)
        );

        forPlugin.AddOrUpdate(refusal.Code, 1, static (_, existing) => existing + 1);
    }

    public IReadOnlyList<KeyValuePair<string, int>> Counts(Ulid pluginId)
    {
        if (!_counts.TryGetValue(pluginId, out ConcurrentDictionary<string, int>? forPlugin))
            return [];

        return
        [
            .. forPlugin
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal),
        ];
    }

    public void Clear(Ulid pluginId) => _counts.TryRemove(pluginId, out _);
}
