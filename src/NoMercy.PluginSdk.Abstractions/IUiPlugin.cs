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
/// A plugin that puts something on screen.
/// <para>
/// The plugin describes what it wants shown and the client decides how that
/// looks on a phone, a browser and a television. A plugin never ships UI code
/// for a platform it cannot test on.
/// </para>
/// </summary>
public interface IUiPlugin : IPlugin
{
    /// <summary>
    /// Where this plugin appears in navigation. Usually one entry; more than
    /// one when the plugin genuinely belongs in two places.
    /// </summary>
    IReadOnlyList<PluginNavEntry> NavEntries { get; }

    /// <summary>
    /// The view for one route. Called per request, so a plugin returns current
    /// state rather than caching a tree and going stale.
    /// </summary>
    /// <summary>
    /// The pages this plugin serves.
    ///
    /// Empty by default, which is what a plugin that string-matches its own
    /// paths has always done and keeps doing. Declaring the table instead lets
    /// the server list the pages a viewer can reach, hand the client the shell
    /// each page wants, and refuse a link to a page that does not exist.
    /// </summary>
    PluginRouteTable Routes => new();

    Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct);
}
