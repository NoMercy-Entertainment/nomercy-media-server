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
/// One tile on a shelf, drawn as the app draws an artist or an album
/// (<c>NMMusicCard</c>). Built with <see cref="PluginShelves.MusicCard" />.
/// </summary>
public sealed record PluginShelfCard
{
    /// <summary>The node id. Unique in the view, so scope it when one item appears twice.</summary>
    public required string Id { get; init; }

    /// <summary>The first line under the picture.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Where the card goes, as an app path such as
    /// <c>/music/plugins/{pluginId}/stations/42</c>. The card is a link, the same
    /// as every card in the app. <see cref="PluginKind.PrefixFor" /> gives the
    /// start of that path.
    /// </summary>
    public required string Link { get; init; }

    /// <summary>
    /// The second line. Required on purpose, even when it is null: left out, the
    /// card writes its <see cref="Type" /> there instead, so every station read
    /// "Artist".
    /// </summary>
    public required string? Description { get; init; }

    /// <summary>The picture: a data URI or a link the host minted. Null draws the card's placeholder.</summary>
    public string? Cover { get; init; }

    /// <summary>
    /// The shape of the picture: <c>artist</c> is a framed square, <c>album</c> a
    /// record sleeve. Null draws the plain square.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>The card's right-click menu, in the shape the app's own cards read.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ContextMenu { get; init; } = [];
}
