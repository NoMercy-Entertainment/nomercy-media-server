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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Where a plugin puts a password.
/// <para>
/// The rule is that secrets are protected and stored in the database, never in
/// config and never in an environment variable. A plugin had no way to obey it:
/// <see cref="IPluginConfiguration"/> is whole-object JSON on disk, so a
/// password written through it lands in plaintext, and the alternative was to
/// resolve a data-protection provider out of the service provider and hand-roll
/// the rest.
/// </para>
/// <para>
/// The store is scoped to the calling plugin. Keys are namespaced by plugin id
/// by the implementation rather than by the caller, so one plugin cannot read
/// another's secrets by guessing a key, and a plugin cannot widen its own scope
/// by choosing a clever one.
/// </para>
/// </summary>
public interface IPluginSecretStore
{
    /// <summary>The value, or null when the key was never set.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Stores <paramref name="value"/> protected at rest.</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Removes the key. Removing a key that is not there is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>The keys this plugin has set. Names only — never the values.</summary>
    Task<IReadOnlyList<string>> KeysAsync(CancellationToken ct = default);

    /// <summary>
    /// The caller's own secret. A provider login that every member shares is
    /// one that cannot be revoked for one of them, so a per-user credential is
    /// held per user rather than in the server's own slot.
    /// </summary>
    Task<string?> GetForUserAsync(string key, CancellationToken ct = default);

    Task SetForUserAsync(string key, string value, CancellationToken ct = default);

    Task DeleteForUserAsync(string key, CancellationToken ct = default);
}
