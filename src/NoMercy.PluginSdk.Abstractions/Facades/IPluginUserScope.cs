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
/// The caller's own corner of this plugin's storage.
/// <para>
/// Separate from the plugin's own storage rather than a folder inside it, so
/// two things are possible that are otherwise guesswork: handing one person
/// everything a plugin holds about them, and removing it when they leave. A
/// plugin that mixes user data into its own files can do neither, and nobody
/// finds out until someone asks.
/// </para>
/// </summary>
public interface IPluginUserScope
{
    /// <summary>Files, private to the caller.</summary>
    IPluginStorageScope Files { get; }

    /// <summary>A database, private to the caller.</summary>
    Task<IPluginDatabase> OpenDatabaseAsync(string name, CancellationToken ct = default);

    /// <summary>Everything in the scope, in a form the person can be handed.</summary>
    Task<PluginUserScopeExport> ExportAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes it. Called by the host when the person leaves, not by the
    /// plugin: erasure that depends on every plugin remembering to do it is
    /// erasure that will be missed.
    /// </summary>
    Task PurgeAsync(CancellationToken ct = default);
}
