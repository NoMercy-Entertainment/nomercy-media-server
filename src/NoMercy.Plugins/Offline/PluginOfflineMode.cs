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
using NoMercy.NmSystem.Information;

namespace NoMercy.Plugins.Offline;

/// <summary>
/// The owner's answer for a server that will never reach NoMercy.
/// <para>
/// It changes where the entitlements and the revocation list come from, and
/// nothing else about how a plugin is checked. A bundle carried in on a stick
/// is answering the same two questions a connected server asks; it is not a
/// way of skipping them.
/// </para>
/// </summary>
public sealed record PluginOfflineMode(bool Enabled, int BundleValidityDays)
{
    public const int DefaultBundleValidityDays = 30;

    public static PluginOfflineMode Off { get; } = new(false, DefaultBundleValidityDays);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static string FileFor(string folder) => Path.Combine(folder, "offline.json");

    public static PluginOfflineMode Load(string? folder = null)
    {
        string file = FileFor(folder ?? AppFiles.PluginConfigPath);

        if (!File.Exists(file))
            return Off;

        try
        {
            return JsonSerializer.Deserialize<PluginOfflineMode>(File.ReadAllText(file), Json)
                ?? Off;
        }
        catch (JsonException)
        {
            // A setting we cannot read is a setting nobody chose, and off is
            // the state that asks NoMercy rather than the one that does not.
            return Off;
        }
    }

    public void Save(string? folder = null)
    {
        string root = folder ?? AppFiles.PluginConfigPath;
        Directory.CreateDirectory(root);
        File.WriteAllText(FileFor(root), JsonSerializer.Serialize(this, Json));
    }
}
