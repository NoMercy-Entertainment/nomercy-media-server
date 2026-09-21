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
/// The host-owned management area every plugin has at {prefix}/{id}/_/...
///
/// The plugin never renders these pages. What a plugin is allowed, what it
/// costs, how to remove it and whether it is healthy are answers the owner has
/// to be able to trust, and an answer drawn by the thing being asked about is
/// not one.
/// </summary>
public static class PluginBasePathPage
{
    public const string Info = "info";
    public const string Permissions = "permissions";
    public const string Settings = "settings";
    public const string Update = "update";
    public const string Remove = "remove";
    public const string Docs = "docs";
    public const string License = "license";
    public const string Health = "health";

    public static IReadOnlyList<string> All { get; } =
    [Info, Permissions, Settings, Update, Remove, Docs, License, Health];

    /// <summary>
    /// The pages only the owner sees. The rest are open to anyone the plugin
    /// is shared with: what a plugin is and what it does are not secrets, and
    /// a member who cannot read them cannot ask for it sensibly.
    /// </summary>
    public static IReadOnlyList<string> OwnerOnly { get; } = [Permissions, Update, Remove, Health];

    public static bool IsKnown(string? page)
    {
        return page is not null && All.Contains(page);
    }
}
