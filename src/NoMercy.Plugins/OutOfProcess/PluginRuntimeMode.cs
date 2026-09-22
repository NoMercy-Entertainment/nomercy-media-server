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

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>Where a plugin runs.</summary>
public enum PluginIsolation
{
    /// <summary>In the server's own process, as every plugin did before.</summary>
    InProcess,

    /// <summary>One process per plugin, talking over its own channel.</summary>
    OutOfProcess,
}

/// <summary>
/// Whether this server runs plugins in their own processes yet.
/// <para>
/// In-process by default, and in-process when the setting cannot be read. The
/// out-of-process runtime is the safer place for a plugin to be, but it is the
/// newer path: defaulting a working server onto it because a settings file was
/// unreadable would turn a bad read into every plugin behaving differently.
/// </para>
/// <para>
/// Per plugin as well as globally, because the two questions are different.
/// Moving every plugin at once is the migration; moving one is how somebody
/// tries the new runtime on a plugin they can afford to have stop.
/// </para>
/// </summary>
public sealed record PluginRuntimeMode(
    PluginIsolation Default = PluginIsolation.InProcess,
    IReadOnlyDictionary<string, PluginIsolation>? PerPlugin = null
)
{
    public static PluginRuntimeMode InProcess { get; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public PluginIsolation For(Ulid pluginId) =>
        PerPlugin?.TryGetValue(pluginId.ToString(), out PluginIsolation isolation) == true
            ? isolation
            : Default;

    private static string FileFor(string folder) => Path.Combine(folder, "runtime-mode.json");

    public static PluginRuntimeMode Load(string? folder = null)
    {
        string file = FileFor(folder ?? AppFiles.PluginConfigPath);

        if (!File.Exists(file))
            return InProcess;

        try
        {
            return JsonSerializer.Deserialize<PluginRuntimeMode>(File.ReadAllText(file), Json)
                ?? InProcess;
        }
        catch (JsonException)
        {
            return InProcess;
        }
    }

    public void Save(string? folder = null)
    {
        string root = folder ?? AppFiles.PluginConfigPath;
        Directory.CreateDirectory(root);
        File.WriteAllText(FileFor(root), JsonSerializer.Serialize(this, Json));
    }
}
