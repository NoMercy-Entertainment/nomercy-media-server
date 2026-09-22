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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.DTOs.Dashboard;

/// <summary>
/// Everything a client needs to put a plugin in its navigation, without asking
/// the plugin for a view first.
/// </summary>
public record PluginUiDescriptorDto
{
    [JsonProperty("id")]
    public required Ulid Id { get; init; }

    [JsonProperty("name")]
    public required string Name { get; init; }

    [JsonProperty("version")]
    public required string Version { get; init; }

    [JsonProperty("verified")]
    public bool Verified { get; init; }

    [JsonProperty("nav_entries")]
    public IEnumerable<PluginNavEntryDto> NavEntries { get; init; } = [];

    /// <summary>
    /// Whether this plugin declared the live channel. A client that knows it
    /// will not push subscribes once instead of polling the view.
    /// </summary>
    [JsonProperty("supports_hub")]
    public bool SupportsHub { get; init; }

    [JsonProperty("supports_rest")]
    public bool SupportsRest { get; init; }

    /// <summary>
    /// Whether this is the caller's own or shared with them by the owner. A
    /// client built before this field ignores it and keeps rendering, and the
    /// entries it no longer receives are the ones it should never have shown.
    /// </summary>
    [JsonProperty("access")]
    public string Access { get; init; } = "owned";

    public static PluginUiDescriptorDto From(
        PluginInfo info,
        IUiPlugin? plugin,
        PluginAccess access = PluginAccess.Owned
    ) =>
        new()
        {
            Access = access.ToString().ToLowerInvariant(),
            Id = info.Id,
            Name = info.Name,
            Version = info.Version.ToString(),
            Verified = info.Verified,
            SupportsHub = info.Capabilities?.Ws ?? false,
            SupportsRest = info.Capabilities?.Rest ?? false,
            NavEntries = Navigation(info, plugin),
        };

    /// <summary>
    /// The plugin's own entries when it is loaded, falling back to the mounts
    /// it declared in its manifest.
    /// <para>
    /// The manifest is what an owner reviewed at consent time, so it is the
    /// honest answer for a plugin that has not been initialised yet — and a
    /// plugin that lists nowhere to go would otherwise be installed, enabled,
    /// and invisible.
    /// </para>
    /// </summary>
    private static IEnumerable<PluginNavEntryDto> Navigation(PluginInfo info, IUiPlugin? plugin)
    {
        if (plugin is not null && plugin.NavEntries.Count > 0)
            return plugin.NavEntries.Select(PluginNavEntryDto.From);

        return (info.Capabilities?.Ui?.Mounts ?? []).Select(PluginNavEntryDto.From);
    }
}
