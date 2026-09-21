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

namespace NoMercy.Api.DTOs.Plugins;

/// <summary>
/// One thing a plugin found.
///
/// <para>
/// It names a route in the plugin's own table rather than a url, so opening it
/// is the same structured navigation as any other placement and no client has
/// to know how that plugin's pages are addressed.
/// </para>
/// </summary>
public record PluginSearchResultDto
{
    [JsonProperty("id")]
    public string Id { get; init; } = string.Empty;

    [JsonProperty("title")]
    public string Title { get; init; } = string.Empty;

    [JsonProperty("subtitle")]
    public string? Subtitle { get; init; }

    [JsonProperty("cover")]
    public string? Cover { get; init; }

    [JsonProperty("route")]
    public string Route { get; init; } = string.Empty;

    [JsonProperty("params")]
    public IReadOnlyDictionary<string, string> Params { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// What one plugin answered with, kept as its own group.
///
/// <para>
/// Never mixed into the app's own groups: a plugin's answer in the row headed
/// Movies reads as something the library has, and once merged there is no way
/// to tell the two apart again.
/// </para>
/// </summary>
public record PluginSearchGroupDto
{
    [JsonProperty("plugin_id")]
    public string PluginId { get; init; } = string.Empty;

    [JsonProperty("plugin_name")]
    public string PluginName { get; init; } = string.Empty;

    [JsonProperty("results")]
    public IReadOnlyList<PluginSearchResultDto> Results { get; init; } = [];
}
