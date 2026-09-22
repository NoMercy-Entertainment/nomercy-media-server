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
/// A headless browser the platform ships and sandboxes.
/// <para>
/// The plugin names a page and reads it. It never downloads a browser, never
/// starts Xvfb and never gets a command line, because all three are process
/// spawn wearing a different coat, and the owner who granted a browser did not
/// grant an arbitrary binary.
/// </para>
/// <para>
/// The browser does not widen the network grant, it rides on it. Navigating to
/// a host the plugin's <c>network.fetch</c> globs do not match refuses with
/// <see cref="PluginRefusalCodes.BrowserNavigationBlocked" />, so a page cannot
/// become the way around a host list the owner reviewed.
/// </para>
/// </summary>
public interface IPluginBrowser
{
    Task<IPluginBrowserPage> OpenAsync(
        Uri url,
        PluginBrowserOptions options,
        CancellationToken ct = default
    );
}
