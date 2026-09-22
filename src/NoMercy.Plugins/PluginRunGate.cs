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
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk;

/// <summary>One reason a plugin might not run. The order they are registered in is the order they are asked.</summary>
public interface IPluginRunCheck
{
    PluginRefusal? Check(PluginInfo plugin);
}

/// <summary>Whether a plugin may run at all.</summary>
public interface IPluginRunGate
{
    /// <summary>
    /// Asked with the facts rather than an id, so the loader can ask before
    /// the plugin is in the registry. A gate that could only be asked about a
    /// plugin the server already knows could not stop one from starting.
    /// </summary>
    PluginRefusal? MayRun(PluginInfo plugin);

    PluginRefusal? MayRun(Ulid pluginId);

    PluginRefusal? LastRefusal(Ulid pluginId);
}

/// <summary>
/// The four questions asked before a plugin runs, in the order the owner
/// needs the answer in.
/// <para>
/// Order is the whole point. A withdrawn build that is also unpaid must say
/// it was withdrawn: an owner told to buy something would buy it and watch
/// nothing change. First reason wins, and the rest are not asked.
/// </para>
/// <para>
/// The signature and the sideload policy are not here. Those are asked when a
/// package arrives, and asking them again at start would be re-litigating an
/// install the owner already made.
/// </para>
/// </summary>
public class PluginRunGate(
    IReadOnlyList<IPluginRunCheck> checks,
    IPluginManifestSource? plugins = null
) : IPluginRunGate
{
    private readonly ConcurrentDictionary<Ulid, PluginRefusal> _last = new();

    public PluginRefusal? MayRun(PluginInfo plugin)
    {
        foreach (IPluginRunCheck check in checks)
        {
            if (check.Check(plugin) is not { } refusal)
                continue;

            _last[plugin.Id] = refusal;

            return refusal;
        }

        // Cleared rather than left: a plugin that was refused last week and
        // runs today would otherwise show last week's reason on its page.
        _last.TryRemove(plugin.Id, out _);

        return null;
    }

    /// <summary>
    /// The same question about a plugin the server already has. One it does
    /// not have is not refused: there is nothing to stop.
    /// </summary>
    public PluginRefusal? MayRun(Ulid pluginId) =>
        plugins?.Find(pluginId) is { } plugin ? MayRun(plugin) : null;

    public PluginRefusal? LastRefusal(Ulid pluginId) =>
        _last.TryGetValue(pluginId, out PluginRefusal? refusal) ? refusal : null;
}
