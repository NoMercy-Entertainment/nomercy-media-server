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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// The caller's own data, and nothing else's.
/// No member takes a user id: a plugin that wants another person's history has
/// to ask that person, which is the whole point of a per-user scope.
/// </summary>
public interface IPluginUserData
{
    Task<PluginUserIdentity> IdentityAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PluginWatchEntry>> WatchAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PluginPlaylist>> PlaylistsAsync(CancellationToken ct = default);

    Task<PluginUserPreferences> PreferencesAsync(CancellationToken ct = default);
}
