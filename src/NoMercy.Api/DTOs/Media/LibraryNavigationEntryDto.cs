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

using Newtonsoft.Json;

namespace NoMercy.Api.DTOs.Media;

/// <summary>
/// One way into the library section, as the server places it.
///
/// <para>
/// The client used to hold this list itself: a library loop followed by seven
/// hard-coded pages and a plugin loop, repeated per surface and drifting between
/// them. It cannot be held there, because which of them exist is the server's
/// answer — a viewer granted only music has no people or specials to browse, and
/// a plugin's page exists only while the plugin is enabled. So the section states
/// its own entries, in the order they are drawn, and a client draws what it is
/// given.
/// </para>
/// </summary>
public record LibraryNavigationEntryDto
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// A library's own name, or a translation key for a page the app ships.
    /// A key is recognisable by its dots and is passed through the client's
    /// dictionary; a name is drawn as it stands.
    /// </summary>
    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;

    [JsonProperty("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonProperty("link")]
    public string Link { get; set; } = string.Empty;

    /// <summary>Where the entry came from: a library, a page the app ships, or a plugin.</summary>
    [JsonProperty("origin")]
    public string Origin { get; set; } = LibraryNavigationOrigin.Library;

    [JsonProperty("plugin_id", NullValueHandling = NullValueHandling.Ignore)]
    public Ulid? PluginId { get; set; }

    [JsonProperty("route_type")]
    public string RouteType { get; set; } = string.Empty;
}
