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

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>Why a plugin process is being started again.</summary>
public enum PluginRestartCause
{
    Requested,
    Crashed,
    MemoryQuota,
    Updated,
}

/// <summary>
/// How often a plugin has crashed lately, and whether it is worth starting
/// again.
/// <para>
/// A plugin that crashes on a line it runs at startup crashes again the
/// instant it is restarted. Restarting it without a ceiling turns one broken
/// plugin into a process the server spawns forever, which costs more than the
/// plugin was ever worth and buries the reason in a log nobody can read.
/// </para>
/// <para>
/// The window matters as much as the count. A plugin that crashed three times
/// last month is not the same plugin as one that crashed three times in the
/// last minute, and only the second is unfit to run.
/// </para>
/// </summary>
public sealed class PluginRestartLedger(int limit = 3, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(5);
    private readonly Dictionary<Ulid, List<DateTimeOffset>> _crashes = new();
    private readonly Lock _gate = new();

    /// <summary>A deliberate restart is not a crash and never counts.</summary>
    public bool ShouldRestart(Ulid pluginId, PluginRestartCause cause, DateTimeOffset now)
    {
        if (cause != PluginRestartCause.Crashed)
            return true;

        lock (_gate)
        {
            if (!_crashes.TryGetValue(pluginId, out List<DateTimeOffset>? times))
                _crashes[pluginId] = times = [];

            times.Add(now);
            times.RemoveAll(at => now - at > _window);

            return times.Count < limit;
        }
    }

    /// <summary>
    /// Called when a plugin has run long enough to be considered healthy, so
    /// an old crash cannot combine with a new one months later to stop a
    /// plugin that has been fine in between.
    /// </summary>
    public void Forget(Ulid pluginId)
    {
        lock (_gate)
            _crashes.Remove(pluginId);
    }

    public int CrashesInWindow(Ulid pluginId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_crashes.TryGetValue(pluginId, out List<DateTimeOffset>? times))
                return 0;

            return times.Count(at => now - at <= _window);
        }
    }
}
