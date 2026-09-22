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

using System.Text.Json.Serialization;

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Where a plugin appears in a client's navigation, and what route the client
/// asks for when the user goes there.
/// </summary>
public class PluginNavEntry
{
    /// <summary>
    /// One of <see cref="PluginUiSection"/>. An unknown value is not an error;
    /// a client that does not recognise it falls back to
    /// <see cref="PluginUiSection.Addon"/>.
    /// </summary>
    [JsonPropertyName("section")]
    public required string Section { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    [JsonPropertyName("route")]
    public required string Route { get; init; }

    /// <summary>
    /// Where inside the section this entry draws, from <see cref="PluginSlot" />.
    ///
    /// A navigation button is the default because it is the only placement
    /// every client has always drawn, so an entry that says nothing lands
    /// where it used to rather than nowhere.
    /// </summary>
    [JsonPropertyName("slot")]
    public string Slot { get; init; } = PluginSlot.Nav;

    /// <summary>
    /// What a caller needs to open it. An owner entry stays out of a member's
    /// navigation, because offering a page that will answer 403 is worse than
    /// not offering it.
    /// </summary>
    [JsonPropertyName("access")]
    public PluginRouteAccess Access { get; init; } = PluginRouteAccess.Shared;

    /// <summary>
    /// The surfaces this entry is offered on, from <see cref="PluginSurface" />.
    ///
    /// Empty means every one: an author who says nothing wants their screen
    /// everywhere, not nowhere. Naming a subset is for a page that genuinely
    /// cannot exist elsewhere, and it answers a different question from
    /// branching inside the view — not what the page looks like on a
    /// television, but whether it is offered there at all.
    /// </summary>
    [JsonPropertyName("surfaces")]
    public List<string> Surfaces { get; init; } = [];

    /// <summary>Whether this entry is offered on the given surface.</summary>
    public bool AppearsOn(string surface)
    {
        return Surfaces.Count == 0 || Surfaces.Contains(surface);
    }
}
