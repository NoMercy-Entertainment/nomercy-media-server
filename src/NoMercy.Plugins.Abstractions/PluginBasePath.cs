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
/// The pages the server owns for every plugin.
/// <para>
/// A plugin never renders these. What a plugin is allowed, what it costs, how
/// to remove it and whether it is healthy are answers the owner has to be able
/// to trust, and an answer drawn by the thing being asked about is not one.
/// </para>
/// </summary>
public static class PluginBasePath
{
    public const string Prefix = "_";

    public static IReadOnlyList<string> Pages { get; } = PluginBasePathPage.All;

    /// <summary>
    /// The pages only the owner sees. The rest are open to anyone the plugin
    /// is shared with: what a plugin is and what it does are not secrets, and
    /// a member who cannot read them cannot ask for it sensibly.
    /// </summary>
    public static IReadOnlyList<string> OwnerOnly { get; } = PluginBasePathPage.OwnerOnly;

    public static bool IsReserved(string path) => path.TrimStart('/').Split('/')[0] == Prefix;
}
