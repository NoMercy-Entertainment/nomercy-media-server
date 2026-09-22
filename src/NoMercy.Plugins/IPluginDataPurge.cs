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

namespace NoMercy.PluginSdk;

/// <summary>
/// Removes everything a server holds about one plugin that is not the plugin's
/// own files: its data folder, the owner's consent, every grant, and every
/// secret it stored.
/// <para>
/// Uninstall used to delete only the folder the assembly lived in, so a plugin
/// the owner had thrown away came back on reinstall still consented, still
/// granted, and still holding the passwords it had saved. Removing a plugin has
/// to mean the owner is asked again.
/// </para>
/// </summary>
public interface IPluginDataPurge
{
    Task PurgeAsync(Ulid pluginId, CancellationToken ct = default);
}
