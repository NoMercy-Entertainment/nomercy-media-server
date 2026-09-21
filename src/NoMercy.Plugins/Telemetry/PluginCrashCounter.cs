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

namespace NoMercy.Plugins.Telemetry;

/// <summary>
/// Two numbers per plugin, held in memory for the window they cover.
/// <para>
/// In memory on purpose: a crash count that survives a restart would carry a
/// plugin's worst hour into every hour after it, and the thing being reported
/// is what is happening now.
/// </para>
/// </summary>
public class PluginCrashCounter : IPluginCrashCounter
{
    private readonly ConcurrentDictionary<Ulid, int> _crashes = new();
    private readonly ConcurrentDictionary<Ulid, int> _ceilings = new();

    public void RecordCrash(Ulid pluginId) =>
        _crashes.AddOrUpdate(pluginId, 1, (_, seen) => seen + 1);

    public void RecordCeilingHit(Ulid pluginId) =>
        _ceilings.AddOrUpdate(pluginId, 1, (_, seen) => seen + 1);

    public int CrashesFor(Ulid pluginId) => _crashes.TryGetValue(pluginId, out int seen) ? seen : 0;

    public int CeilingHitsFor(Ulid pluginId) =>
        _ceilings.TryGetValue(pluginId, out int seen) ? seen : 0;

    public void Reset()
    {
        _crashes.Clear();
        _ceilings.Clear();
    }
}
