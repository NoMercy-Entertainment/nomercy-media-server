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
/// What a plugin is, which decides where its screens live.
///
/// A plugin never invents a path. It says what it is, and the kind decides the
/// prefix on every platform, so the same plugin lands in the music area on the
/// web, on a phone and on a television without any of them agreeing on anything
/// beyond this word. A plugin that could name its own route would collide with
/// the app's own pages, and no client could route a plugin it had never seen.
/// </summary>
public static class PluginKind
{
    /// <summary>Lives with the music library.</summary>
    public const string Music = "music";

    /// <summary>Lives with films and shows.</summary>
    public const string Video = "video";

    /// <summary>Applies to the library as a whole rather than one medium.</summary>
    public const string Library = "library";

    /// <summary>An administrative screen, the only home plugins used to have.</summary>
    public const string Dashboard = "dashboard";

    /// <summary>
    /// Configuration for the plugin itself. Distinct from the dashboard, which
    /// is where a plugin does its work rather than where it is set up, and the
    /// place a viewer looks first when a plugin needs a key or a folder.
    /// </summary>
    public const string Settings = "settings";

    /// <summary>
    /// Big enough to stand on its own, with a button in the main navigation
    /// beside the app's own sections rather than tucked inside one of them.
    /// </summary>
    public const string Addon = "addon";

    public static readonly string[] All = [Music, Video, Library, Dashboard, Settings, Addon];

    /// <summary>
    /// A plugin with no interface declares no mounts. There is no `backend`
    /// kind, because backend is not a place a screen goes: it is the absence of
    /// one, and saying it twice would let a manifest contradict itself.
    /// </summary>
    public static bool IsKnown(string? kind)
    {
        return kind is not null && Array.IndexOf(All, kind) >= 0;
    }

    /// <summary>Whether a mount of this kind can be placed.</summary>
    public static bool DrawsUi(string? kind)
    {
        return IsKnown(kind);
    }

    /// <summary>
    /// Whether this kind earns its own entry in the main navigation rather than
    /// appearing inside a section the app already has.
    /// </summary>
    public static bool IsTopLevel(string? kind)
    {
        return kind == Addon;
    }
}

/// <summary>
/// Where a plugin's screens hang on every client.
/// </summary>
public static class PluginRoutes
{
    /// <summary>
    /// The prefix for a plugin of this kind.
    ///
    /// Every client owns one wildcard route per prefix and hands whatever
    /// follows to the plugin as its own route. That is what lets a television
    /// app, which cannot add routes while running, still show a plugin nobody
    /// had heard of when the app shipped.
    /// </summary>
    public static string PrefixFor(string kind, Ulid pluginId)
    {
        if (!PluginKind.DrawsUi(kind))
            throw new ArgumentException($"plugins of kind '{kind}' have no screens", nameof(kind));

        return kind == PluginKind.Addon ? $"/addons/{pluginId}" : $"/{kind}/plugins/{pluginId}";
    }

    /// <summary>
    /// The wildcard a client registers once per kind, with no plugin in mind.
    /// </summary>
    public static string PatternFor(string kind)
    {
        if (!PluginKind.DrawsUi(kind))
            throw new ArgumentException($"plugins of kind '{kind}' have no screens", nameof(kind));

        return kind == PluginKind.Addon
            ? "/addons/:pluginId/:route*"
            : $"/{kind}/plugins/:pluginId/:route*";
    }
}
