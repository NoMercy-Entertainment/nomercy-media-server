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

public record PluginNavEntryDto
{
    /// <summary>
    /// Always one a client knows: an unrecognised section is resolved to
    /// <see cref="PluginUiSection.Addon"/> here rather than shipped through and
    /// dropped by whichever client happens not to know it.
    /// </summary>
    [JsonProperty("section")]
    public required string Section { get; init; }

    [JsonProperty("label")]
    public required string Label { get; init; }

    [JsonProperty("icon")]
    public string? Icon { get; init; }

    [JsonProperty("route")]
    public required string Route { get; init; }

    /// <summary>
    /// Where inside the section this entry draws, from <see cref="PluginSlot" />.
    /// A client built before slots ignores it and keeps drawing a button.
    /// </summary>
    [JsonProperty("slot")]
    public string Slot { get; init; } = PluginSlot.Nav;

    /// <summary>
    /// What a caller needs to open it. A client that hides an owner entry from
    /// a member stops offering a page the server would answer 403 for.
    /// </summary>
    [JsonProperty("access")]
    public string Access { get; init; } = "shared";

    public static PluginNavEntryDto From(PluginNavEntry entry) =>
        new()
        {
            Section = PluginUiSection.OrFallback(entry.Section),
            Label = entry.Label,
            Icon = entry.Icon,
            Route = entry.Route,
            Slot = entry.Slot,
            Access = entry.Access.ToString().ToLowerInvariant(),
        };

    public static PluginNavEntryDto From(PluginUiMount mount) =>
        new()
        {
            Section = PluginUiSection.OrFallback(mount.Section),
            Label = mount.Label,
            Icon = mount.Icon,
            Route = mount.Route,
            Slot = mount.Slot,
            Access = mount.Access.ToString().ToLowerInvariant(),
        };
}
