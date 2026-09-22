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
/// One playable thing, drawn by the client's own card rather than by markup
/// the plugin shipped.
/// <para>
/// It carries an id, not a URL. A card that carried a URL was a card a plugin
/// had to mint a link for before anyone had asked to play it, and those links
/// expire while the viewer is still scrolling.
/// </para>
/// </summary>
public sealed record PluginMediaCard
{
    /// <summary>Set for library media. Null for a live channel.</summary>
    public MediaId? Media { get; init; }

    /// <summary>Set for a live channel. Null for library media.</summary>
    public string? ChannelId { get; init; }

    public required string Title { get; init; }
    public string? Artist { get; init; }
    public Uri? Cover { get; init; }
}
