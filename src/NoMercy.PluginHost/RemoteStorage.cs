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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.PluginHost;

/// <summary>
/// The plugin's folders, named by the server and opened by the plugin.
/// <para>
/// Bytes do not cross the channel. A transcode written through the server
/// would copy every byte twice, hold a server thread for the length of the
/// write, and have the server doing file I/O on behalf of code it does not
/// trust. The server answers which folder, once, and this process opens the
/// files itself.
/// </para>
/// <para>
/// The same <see cref="PluginLocalStorageScope" /> guard runs here as in the
/// server, rather than a second copy written for this side: an absolute path
/// and a <c>..</c> segment are the two ways out of a scope, and a security
/// guard maintained twice is one that will be fixed once.
/// </para>
/// </summary>
public sealed class RemoteStorage(Ulid pluginId, RemoteCall call) : IPluginStorage
{
    private readonly Dictionary<string, IPluginStorageScope> _scopes = [];
    private readonly Lock _gate = new();

    public IPluginStorageScope Private => Fixed(nameof(IPluginStorage.Private));

    public IPluginStorageScope Temp => Fixed(nameof(IPluginStorage.Temp));

    public IPluginStorageScope Derived => Fixed(nameof(IPluginStorage.Derived));

    public async Task<IPluginStorageScope> PathAsync(
        string folderId,
        CancellationToken ct = default
    )
    {
        string root =
            await call.AskAsync<string>(
                "storage",
                nameof(IPluginStorage.PathAsync),
                new { folderId }
            )
            ?? throw new PluginRefusedException(
                PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), folderId)
            );

        return new PluginLocalStorageScope(pluginId, root);
    }

    /// <summary>
    /// Per-user storage and the plugin's own database cross the boundary in
    /// their own task. A half-connected facade that answered an empty scope
    /// would look to a plugin exactly like a user who had never saved
    /// anything.
    /// </summary>
    public IPluginUserScope ForUser =>
        throw new PluginRefusedException(NotYet(nameof(IPluginStorage.ForUser)));

    public Task<IPluginDatabase> OpenDatabaseAsync(string name, CancellationToken ct = default) =>
        throw new PluginRefusedException(NotYet(nameof(IPluginStorage.OpenDatabaseAsync)));

    /// <summary>
    /// The plugin's own three folders never change for the life of the
    /// process, so each is asked for once. Asking again would make every read
    /// pay for the boundary for an answer that cannot have moved.
    /// </summary>
    private IPluginStorageScope Fixed(string member)
    {
        lock (_gate)
        {
            if (_scopes.TryGetValue(member, out IPluginStorageScope? scope))
                return scope;

            string root = call.Ask<string>("storage", member) ?? string.Empty;

            IPluginStorageScope made = new PluginLocalStorageScope(pluginId, root);
            _scopes[member] = made;

            return made;
        }
    }

    private PluginRefusal NotYet(string member) =>
        new(
            PluginRefusalCodes.HostServicesRemoved,
            pluginId.ToString(),
            $"A plugin in its own process asked for storage.{member}.",
            "This server runs the plugin out of process, and that member does not cross the boundary yet.",
            "Run this plugin in the server's own process until it is carried across. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            PluginRefusalSeverity.Blocked
        );
}
