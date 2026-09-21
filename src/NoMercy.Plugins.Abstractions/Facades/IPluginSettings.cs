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
/// The values behind the settings page the host renders from the plugin's
/// schema.
/// </summary>
public interface IPluginSettings
{
    T? Get<T>(string key);

    /// <summary>The caller's own value for a user-scoped field.</summary>
    T? GetForUser<T>(string key);

    Task SetAsync<T>(string key, T value, CancellationToken ct = default);

    Task SetForUserAsync<T>(string key, T value, CancellationToken ct = default);

    /// <summary>
    /// Raised when a value changes, so a plugin holding a connection built from
    /// a setting rebuilds it rather than serving the old one until a restart.
    /// </summary>
    event EventHandler<string> SettingsChanged;
}
