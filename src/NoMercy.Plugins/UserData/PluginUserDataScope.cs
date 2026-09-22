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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.UserData;

/// <summary>
/// One person's corner of one plugin's storage.
/// <para>
/// A folder per person rather than a column in the plugin's own files,
/// because two things depend on that shape: handing somebody everything a
/// plugin holds about them, and removing it when they leave. A plugin that
/// mixed the two could do neither, and nobody would find out until it was
/// asked.
/// </para>
/// </summary>
public class PluginUserDataScope(string root, Ulid pluginId, Guid userId)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Folder { get; } = FolderFor(root, pluginId, userId);

    public static string FolderFor(string root, Ulid pluginId, Guid userId) =>
        Path.Combine(root, pluginId.ToString(), "users", userId.ToString());

    /// <summary>
    /// What a `user.*` capability is refused with when nobody is asking.
    /// <para>
    /// Refused rather than written somewhere shared. A scheduled job has no
    /// person behind it, and the alternative is a plugin quietly keeping one
    /// pile of everybody's history, which is exactly what this shape exists
    /// to prevent.
    /// </para>
    /// </summary>
    public static PluginRefusal? Require(Guid? callerId, Ulid pluginId)
    {
        if (callerId is { } asking && asking != Guid.Empty)
            return null;

        return new(
            PluginRefusalCodes.UserDataScopeRequired,
            pluginId.ToString(),
            "The plugin asked for somebody's own data with nobody asking.",
            "Everything under a user capability is stored per user, so there has to be a person for it to belong to.",
            "Call it while serving a request. A scheduled job has no viewer, so it has no user data to read.",
            PluginRefusalSeverity.Blocked
        );
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        string file = FileFor(key);

        if (!File.Exists(file))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(file, ct), Json);
        }
        catch (JsonException)
        {
            // A value nobody can read is a value that is not there. Throwing
            // would take a plugin down over one key it wrote badly once.
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);

        await File.WriteAllTextAsync(FileFor(key), JsonSerializer.Serialize(value, Json), ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        string file = FileFor(key);

        if (File.Exists(file))
            File.Delete(file);

        return Task.CompletedTask;
    }

    /// <summary>
    /// A key becomes a file name, so it may not climb out of the folder. A
    /// key containing a separator is the one way a plugin could read another
    /// person's scope by writing a path rather than a name.
    /// </summary>
    private string FileFor(string key) => Path.Combine(Folder, $"{Uri.EscapeDataString(key)}.json");
}
