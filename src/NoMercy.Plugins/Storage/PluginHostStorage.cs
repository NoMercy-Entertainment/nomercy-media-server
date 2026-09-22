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
using NoMercy.PluginSdk.Quotas;

namespace NoMercy.PluginSdk.Storage;

/// <summary>
/// Every place one plugin may read and write.
/// <para>
/// Three scopes the server made and hands over, and the owner's own folders
/// reached through the same catalogue the server uses for its own media. The
/// grant is checked here, in one place, rather than in each scope: a folder
/// the owner never granted is not opened at all.
/// </para>
/// </summary>
public class PluginHostStorage : IPluginStorage
{
    private readonly Ulid _pluginId;
    private readonly IPluginFolderCatalog _catalog;
    private readonly IPluginGrantStore _grants;
    private readonly string _databaseRoot;
    private readonly UserId? _userId;

    public PluginHostStorage(
        Ulid pluginId,
        string pluginsRoot,
        IPluginFolderCatalog catalog,
        IPluginGrantStore grants,
        PluginQuotaMeter? quotas = null,
        UserId? userId = null
    )
    {
        _pluginId = pluginId;
        _catalog = catalog;
        _grants = grants;
        _userId = userId;
        _databaseRoot = Path.Combine(pluginsRoot, "data", pluginId.ToString());

        PluginLocalStorageScope privateScope = new(
            pluginId,
            Path.Combine(pluginsRoot, "data", pluginId.ToString())
        );
        PluginLocalStorageScope temporary = new(
            pluginId,
            Path.Combine(pluginsRoot, "temp", pluginId.ToString())
        );
        PluginLocalStorageScope derived = new(
            pluginId,
            Path.Combine(pluginsRoot, "derived", pluginId.ToString())
        );

        privateScope.EnsureExists();
        temporary.EnsureExists();
        derived.EnsureExists();

        // Temp is emptied on every start, which is what makes it temporary.
        // A folder called temp that survives restarts is a folder that grows
        // until a disk fills, and nobody looks in it until then.
        temporary.Purge();

        // Only the private folder is metered. Temp is emptied every start, and
        // derived is the server's own cache, evicted by the server: counting
        // either against the plugin would charge it for the host's decisions.
        Private = quotas is null
            ? privateScope
            : new PluginMeteredStorageScope(privateScope, pluginId, quotas);
        Temp = temporary;
        Derived = derived;
    }

    public IPluginStorageScope Private { get; }

    public IPluginStorageScope Temp { get; }

    public IPluginStorageScope Derived { get; }

    public async Task<IPluginStorageScope> PathAsync(
        string folderId,
        CancellationToken ct = default
    )
    {
        if (!_grants.Holds(_pluginId, Kind, folderId))
            throw Refused(folderId);

        // A folder the owner granted and then removed from the server reads
        // as null. Refused rather than answered with an empty scope, which a
        // plugin would write into and find gone.
        return await _catalog.OpenAsync(folderId, ct) ?? throw Refused(folderId);
    }

    /// <summary>
    /// The caller's own corner, when there is a caller.
    /// <para>
    /// A plugin running as the server rather than for somebody, a scheduled
    /// job for instance, has no per-user scope to hand back. Refusing there
    /// is the honest answer: returning a scope belonging to nobody would let
    /// a job write user data into a folder no export or purge would ever
    /// find.
    /// </para>
    /// </summary>
    public IPluginUserScope ForUser =>
        _userId is { } user
            ? new PluginUserDataScope(_pluginId, user, _databaseRoot)
            : throw new PluginRefusedException(
                PluginRefusalMessages.UserScopeRequired(
                    _pluginId.ToString(),
                    "IPluginStorage.ForUser"
                )
            );

    /// <summary>
    /// A SQLite file in the plugin's private folder, opened by the host.
    /// <para>
    /// The name becomes a file name in that one folder, so it cannot name the
    /// server's own database or anything outside the plugin's corner.
    /// </para>
    /// </summary>
    public async Task<IPluginDatabase> OpenDatabaseAsync(
        string name,
        CancellationToken ct = default
    )
    {
        if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
            throw new PluginRefusedException(
                PluginRefusalMessages.FileOutsideGrant(_pluginId.ToString(), name)
            );

        return await PluginDatabase.OpenAsync(Path.Combine(_databaseRoot, $"{name}.sqlite"), ct);
    }

    private static string Kind => PluginGrantKind.ForCapability(PluginCapabilityNames.StoragePath);

    private PluginRefusedException Refused(string folderId) =>
        new(PluginRefusalMessages.FileOutsideGrant(_pluginId.ToString(), folderId));
}
