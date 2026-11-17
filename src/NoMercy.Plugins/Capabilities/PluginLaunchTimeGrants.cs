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

namespace NoMercy.PluginSdk.Capabilities;

/// <summary>
/// The grant kinds read once, when a plugin's process starts, rather than on
/// every use. Taking one back changes nothing for the running plugin until it
/// restarts, and the owner has to be told that.
/// <para>
/// The only list of them. A kind that starts being read at launch is added
/// here, beside the code that reads it, or the dashboard tells the owner a
/// revoke took effect when it did not.
/// </para>
/// </summary>
public static class PluginLaunchTimeGrants
{
    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        // PluginAssemblyLocation.For: the out-of-process host's sandbox
        // profile, which says whether the plugin may start processes at all,
        // is written from this when the host launches.
        PluginCapabilityNames.ProcessSpawn,
    };

    public static IReadOnlyCollection<string> All => Kinds;

    /// <summary>Whether taking this kind back applies only from the plugin's next start.</summary>
    public static bool AppliesFromNextStart(string kind) => Kinds.Contains(kind);
}
