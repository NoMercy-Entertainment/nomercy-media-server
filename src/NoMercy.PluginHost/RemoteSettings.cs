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

namespace NoMercy.PluginHost;

/// <summary>
/// The plugin's own settings, as the owner set them.
/// <para>
/// The value crosses as JSON and is read into the plugin's own type here. The
/// type lives in the plugin's assembly, which the server does not load, so the
/// server can only carry the shape and not the class.
/// </para>
/// </summary>
public sealed class RemoteSettings(Ulid pluginId, RemoteCall call) : IPluginSettings
{
    public T? Get<T>(string key) =>
        call.Ask<T>("settings", nameof(IPluginSettings.Get), new { key });

    public T? GetForUser<T>(string key) =>
        call.Ask<T>("settings", nameof(IPluginSettings.GetForUser), new { key });

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default) =>
        call.TellAsync("settings", nameof(IPluginSettings.SetAsync), new { key, value });

    public Task SetForUserAsync<T>(string key, T value, CancellationToken ct = default) =>
        call.TellAsync("settings", nameof(IPluginSettings.SetForUserAsync), new { key, value });

    /// <summary>
    /// Never raised out of process.
    /// <para>
    /// The server has no way to call into the child yet, so this would be an
    /// event that silently never fires: a plugin waiting on it would sit there
    /// looking healthy while the owner changed a setting and nothing happened.
    /// The add is refused instead, which says so at the line that subscribes.
    /// </para>
    /// </summary>
    public event EventHandler<string> SettingsChanged
    {
        add => throw new PluginRefusedException(NotYet(nameof(IPluginSettings.SettingsChanged)));
        remove { }
    }

    private PluginRefusal NotYet(string member) =>
        new(
            PluginRefusalCodes.HostServicesRemoved,
            pluginId.ToString(),
            $"A plugin in its own process subscribed to settings.{member}.",
            "The server cannot call into a plugin process yet, so that event would never fire and the plugin would wait forever.",
            "Read the setting when the plugin needs it instead of waiting to be told. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            PluginRefusalSeverity.Blocked
        );
}

/// <summary>
/// What one person has watched, saved and chosen.
/// <para>
/// Nothing is remembered between calls. A watch list is the thing a person is
/// changing while the plugin runs, and a cached one is a plugin showing a
/// resume point the viewer has already passed.
/// </para>
/// </summary>
public sealed class RemoteUserData(RemoteCall call) : IPluginUserData
{
    public async Task<PluginUserIdentity> IdentityAsync(CancellationToken ct = default) =>
        await call.AskAsync<PluginUserIdentity>("user", nameof(IPluginUserData.IdentityAsync))
        ?? throw new PluginRefusedException(NoIdentity());

    public async Task<IReadOnlyList<PluginWatchEntry>> WatchAsync(CancellationToken ct = default) =>
        await call.AskAsync<IReadOnlyList<PluginWatchEntry>>(
            "user",
            nameof(IPluginUserData.WatchAsync)
        ) ?? [];

    public async Task<IReadOnlyList<PluginPlaylist>> PlaylistsAsync(
        CancellationToken ct = default
    ) =>
        await call.AskAsync<IReadOnlyList<PluginPlaylist>>(
            "user",
            nameof(IPluginUserData.PlaylistsAsync)
        ) ?? [];

    public async Task<PluginUserPreferences> PreferencesAsync(CancellationToken ct = default) =>
        await call.AskAsync<PluginUserPreferences>("user", nameof(IPluginUserData.PreferencesAsync))
        ?? throw new PluginRefusedException(NoIdentity());

    /// <summary>
    /// An identity the server could not name is not an empty person. Inventing
    /// one would hand the plugin a user who does not exist, and everything it
    /// then saved would be saved against nobody.
    /// </summary>
    private static PluginRefusal NoIdentity() =>
        new(
            PluginRefusalCodes.UserScopeRequired,
            "unknown plugin",
            "The plugin asked who was using it and the server named nobody.",
            "That call is scoped to one person, and it ran without one.",
            "Call it from a request that carries a caller, such as a view or a REST route. Docs: /nomercy-plugins/capabilities/user-identity",
            PluginRefusalSeverity.Blocked
        );
}
