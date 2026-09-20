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
using System.Text.Json.Nodes;

namespace NoMercy.Plugins.UserData;

/// <summary>
/// Hands a person everything the plugins hold about them, and removes it.
/// <para>
/// The host does both, never the plugin. Erasure that depends on every
/// author remembering to implement it is erasure that gets missed, and the
/// person asking has no way to tell which plugin forgot.
/// </para>
/// </summary>
public class PluginUserDataExporter(string root)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Everything one plugin holds about one person, as JSON.</summary>
    public async Task<string> ExportAsync(
        Ulid pluginId,
        Guid userId,
        CancellationToken ct = default
    )
    {
        string folder = PluginUserDataScope.FolderFor(root, pluginId, userId);
        JsonObject export = [];

        if (!Directory.Exists(folder))
            return export.ToJsonString(Json);

        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            string key = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(file));

            try
            {
                export[key] = JsonNode.Parse(await File.ReadAllTextAsync(file, ct));
            }
            catch (JsonException)
            {
                // Handed over as the text it is. A value this server cannot
                // parse is still that person's, and leaving it out of an
                // export would be deciding what they get to see.
                export[key] = await File.ReadAllTextAsync(file, ct);
            }
        }

        return export.ToJsonString(Json);
    }

    /// <summary>Everything one plugin holds about one person, gone.</summary>
    public void Purge(Ulid pluginId, Guid userId)
    {
        string folder = PluginUserDataScope.FolderFor(root, pluginId, userId);

        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    /// <summary>
    /// The same person, across every plugin on this server. What a member
    /// leaving triggers: one plugin left holding their history is the same
    /// failure as all of them.
    /// </summary>
    public void PurgeEverywhere(Guid userId)
    {
        if (!Directory.Exists(root))
            return;

        foreach (string plugin in Directory.EnumerateDirectories(root))
        {
            string folder = Path.Combine(plugin, "users", userId.ToString());

            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
