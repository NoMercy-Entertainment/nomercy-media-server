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

namespace NoMercy.Plugins.Runtime;

public class PluginResourceLedger : IPluginResourceLedger
{
    private readonly ConcurrentDictionary<Ulid, List<IAsyncDisposable>> _held = new();

    public void Track(Ulid pluginId, IAsyncDisposable resource)
    {
        List<IAsyncDisposable> holding = _held.GetOrAdd(pluginId, _ => []);

        lock (holding)
            holding.Add(resource);
    }

    public void Forget(Ulid pluginId, IAsyncDisposable resource)
    {
        if (!_held.TryGetValue(pluginId, out List<IAsyncDisposable>? holding))
            return;

        lock (holding)
            holding.RemoveAll(entry => ReferenceEquals(entry, resource));
    }

    public int Held(Ulid pluginId)
    {
        if (!_held.TryGetValue(pluginId, out List<IAsyncDisposable>? holding))
            return 0;

        lock (holding)
            return holding.Count;
    }

    public async Task ReleaseAsync(Ulid pluginId, CancellationToken ct = default)
    {
        if (!_held.TryRemove(pluginId, out List<IAsyncDisposable>? holding))
            return;

        IAsyncDisposable[] releasing;

        lock (holding)
        {
            releasing = [.. holding];
            holding.Clear();
        }

        foreach (IAsyncDisposable resource in releasing)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception)
            {
                // A resource that is already gone must not stop the next one
                // from being released. Stopping halfway through is how a
                // plugin keeps a port after it has been uninstalled.
            }
        }
    }
}
