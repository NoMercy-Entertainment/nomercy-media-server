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

namespace NoMercy.PluginSdk.Sideload;

/// <summary>
/// The switch an owner turns on to install a plugin from a file.
/// <para>
/// Off by default and off when the setting cannot be read, because off is the
/// state where the server can say who wrote every plugin it is running.
/// </para>
/// </summary>
public sealed record PluginDeveloperMode(bool Enabled)
{
    public static PluginDeveloperMode Off { get; } = new(false);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static string FileFor(string folder) => Path.Combine(folder, "developer-mode.json");

    public static PluginDeveloperMode Load(string? folder = null)
    {
        string file = FileFor(folder ?? AppFiles.PluginConfigPath);

        if (!File.Exists(file))
            return Off;

        try
        {
            return JsonSerializer.Deserialize<PluginDeveloperMode>(File.ReadAllText(file), Json)
                ?? Off;
        }
        catch (JsonException)
        {
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
