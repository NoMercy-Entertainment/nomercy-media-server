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

using System.Text.Json;
using NoMercy.PluginSdk.Verification;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Makes a hand-staged plugin folder look like one the server installed:
/// the in-folder file record, and the folder's name in the recorded set the
/// server keeps under <c>data/platform</c>. A folder with neither is a manual
/// drop and waits for the owner's approval at boot; tests about anything else
/// stage their folder through here.
/// </summary>
internal static class InstalledPluginFolder
{
    public static async Task RecordAsync(string pluginsDir, string pluginFolder)
    {
        await PluginFileManifest.WriteAsync(pluginFolder);
        await MarkRecordedAsync(pluginsDir, Path.GetFileName(pluginFolder));
    }

    public static void Record(string pluginsDir, string pluginFolder) =>
        RecordAsync(pluginsDir, pluginFolder).GetAwaiter().GetResult();

    /// <summary>The recorded-set half alone, for a test that writes its own record.</summary>
    public static async Task MarkRecordedAsync(string pluginsDir, string folderName)
    {
        string platform = Path.Combine(pluginsDir, "data", "platform");
        Directory.CreateDirectory(platform);
        string path = Path.Combine(platform, "recorded-folders.json");

        HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
            folders.UnionWith(
                JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(path)) ?? []
            );

        folders.Add(folderName);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(folders.Order(StringComparer.OrdinalIgnoreCase))
        );
    }
}
