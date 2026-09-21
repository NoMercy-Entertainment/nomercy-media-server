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
using Microsoft.Data.Sqlite;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Storage;

/// <summary>
/// One person's corner of one plugin's storage.
/// <para>
/// A folder per user under the plugin's own data root, rather than a file the
/// plugin keeps everybody in. Two things follow that are otherwise guesswork:
/// handing one person everything a plugin holds about them, and removing it
/// when they leave. The radio plugin kept every listener's favourites in one
/// file beside its assembly and could do neither.
/// </para>
/// <para>
/// The host owns this, not the plugin. Erasure that depends on every plugin
/// author remembering to implement it is erasure that will be missed.
/// </para>
/// </summary>
public sealed class PluginUserDataScope : IPluginUserScope
{
    private readonly Ulid _pluginId;
    private readonly UserId _userId;
    private readonly string _root;

    public PluginUserDataScope(Ulid pluginId, UserId userId, string pluginDataRoot)
    {
        _pluginId = pluginId;
        _userId = userId;

        // Under "users", so the plugin's own files and its per-user files are
        // never in the same folder. A purge that had to pick between them by
        // name would eventually pick wrong.
        _root = Path.Combine(pluginDataRoot, "users", userId.Value.ToString());

        Directory.CreateDirectory(_root);
        Files = new PluginLocalStorageScope(pluginId, _root);
    }

    public IPluginStorageScope Files { get; }

    /// <summary>
    /// A database in this person's folder, so it is exported and purged with
    /// everything else rather than living somewhere the host does not look.
    /// </summary>
    public async Task<IPluginDatabase> OpenDatabaseAsync(
        string name,
        CancellationToken ct = default
    )
    {
        // The same guard the plugin's own database gets. A name that walked
        // out of the folder would be one person's scope reading another's.
        if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
            throw new PluginRefusedException(
                PluginRefusalMessages.FileOutsideGrant(_pluginId.ToString(), name)
            );

        return await PluginDatabase.OpenAsync(Path.Combine(_root, $"{name}.sqlite"), ct);
    }

    /// <summary>
    /// Everything held for this person, as JSON plus the names of their files.
    /// <para>
    /// Names rather than contents: an export of somebody's downloads should
    /// not have to be built in memory before anybody can read it.
    /// </para>
    /// </summary>
    public async Task<PluginUserScopeExport> ExportAsync(CancellationToken ct = default)
    {
        List<string> files = [];
        Dictionary<string, object?> documents = [];

        await foreach (PluginStorageEntry entry in Files.ListAsync("", true, ct))
        {
            if (entry.IsDirectory)
                continue;

            files.Add(entry.Path);

            // Small JSON is inlined so the export answers the question on its
            // own. Anything else is named and left where it is.
            if (!entry.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.SizeBytes > 1024 * 1024)
                continue;

            await using Stream stream = await Files.OpenReadAsync(entry.Path, ct);
            using StreamReader reader = new(stream);
            string text = await reader.ReadToEndAsync(ct);

            try
            {
                documents[entry.Path] = JsonSerializer.Deserialize<JsonElement>(text);
            }
            catch (JsonException)
            {
                // A file the plugin named .json and did not write as JSON is
                // still the person's data. It travels as text rather than
                // being dropped from their own export.
                documents[entry.Path] = text;
            }
        }

        return new PluginUserScopeExport
        {
            Plugin = new PluginId(_pluginId),
            User = _userId,
            TakenAt = DateTimeOffset.UtcNow,
            Json = JsonSerializer.Serialize(documents),
            Files = files,
        };
    }

    /// <summary>
    /// Removes the whole folder.
    /// <para>
    /// The folder, not its contents one by one: a purge that walked the tree
    /// would leave anything it could not open, and "mostly deleted" is not a
    /// thing you can tell somebody who asked to be forgotten.
    /// </para>
    /// </summary>
    public Task PurgeAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_root))
            return Task.CompletedTask;

        // SQLite keeps pooled handles open after a connection is disposed, so
        // deleting the folder throws while any database the plugin opened is
        // still pooled. An erasure request that fails because somebody once
        // opened a database is an erasure request that did not happen.
        SqliteConnection.ClearAllPools();

        Directory.Delete(_root, true);

        return Task.CompletedTask;
    }
}
