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
/// Where a plugin's UI mount appears.
/// <para>
/// <see cref="PluginUiMount.Section"/> was a required string with no stated
/// values, so an author had to guess and guessing wrong was silent — the mount
/// rendered nowhere, or somewhere unintended, and neither is an error anyone
/// sees. Naming the values makes a typo a compile error instead.
/// </para>
/// <para>
/// An unknown section is NOT rejected. A client that does not know a section
/// falls back to <see cref="Addon"/>, because the alternative is that adding a
/// section to one client turns every plugin using it into a validation failure
/// on the other. Every section here needs a home in both the web sidebar and
/// the KMP navigation; a section one client knows and the other does not is the
/// same silent drift in a new place.
/// </para>
/// <para>
/// These are <c>PluginKind</c>'s values, and they have to stay its values. The
/// list here used to read <c>movies</c>, <c>shows</c> and <c>tools</c>, none of
/// which the server places: a mount declaring one failed
/// <c>PluginKind.IsKnown</c> and was quietly re-homed to the dashboard, so a
/// plugin that asked to appear beside films appeared in the admin panel and its
/// author had no way to find out. The drift this file warns about had happened
/// to the file itself.
/// </para>
/// </summary>
public static class PluginUiSection
{
    /// <summary>Alongside the music library.</summary>
    public const string Music = PluginKind.Music;

    /// <summary>Alongside films and shows.</summary>
    public const string Video = PluginKind.Video;

    /// <summary>In the library section, beside the libraries themselves.</summary>
    public const string Library = PluginKind.Library;

    /// <summary>Server administration, beside the other owner-only panels.</summary>
    public const string Dashboard = PluginKind.Dashboard;

    /// <summary>The plugin's own page under the plugin settings list.</summary>
    public const string Settings = PluginKind.Settings;

    /// <summary>
    /// Reachable from the add-ons page and nowhere else. The fallback for an
    /// unrecognised section, so a mount is always reachable somewhere.
    /// </summary>
    public const string Addon = PluginKind.Addon;

    /// <summary>
    /// The sections every client is expected to render.
    ///
    /// <para>
    /// Taken from <c>PluginKind.All</c> rather than listed again: the two lists
    /// having to agree, and being written twice, is how this file came to name
    /// three sections the server has never placed.
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(PluginKind.All, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="section"/> when a client knows it, otherwise
    /// <see cref="Addon"/>. Renders the mount somewhere real instead of
    /// dropping it.
    /// </summary>
    public static string OrFallback(string? section) =>
        section is not null && All.Contains(section) ? section : Addon;
}
