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

namespace NoMercy.PluginSdk.Runtime;

/// <summary>
/// The binaries the owner granted, by name.
/// <para>
/// A grant holds the absolute path the owner saw, so the name in the manifest
/// is only a label. Nothing here searches PATH or the file system: a name with
/// no grant behind it resolves to nothing, and the spawn refuses.
/// </para>
/// </summary>
public class PluginApprovedBinaries(IPluginGrantStore grants) : IPluginApprovedBinaries
{
    private static string Kind => PluginGrantKind.ForCapability(PluginCapabilityNames.ProcessSpawn);

    public string? PathFor(Ulid pluginId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // Matched on the file name rather than the whole path, because the
        // manifest names a program and the grant records where it lives. The
        // owner granted C:/ffmpeg/bin/ffmpeg.exe; the plugin asks for ffmpeg.
        foreach (string granted in grants.Granted(pluginId, Kind))
        {
            if (!File.Exists(granted))
                continue;

            string file = Path.GetFileNameWithoutExtension(granted);

            if (
                file.Equals(name, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(granted).Equals(name, StringComparison.OrdinalIgnoreCase)
            )
                return granted;
        }

        return null;
    }
}
