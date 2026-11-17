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
/// One home-page row: a title, its cards and where "See more" goes. Drawn as
/// the app's own <c>NMCarousel</c> by <see cref="PluginShelves.Row" /> and
/// chained into a page by <see cref="PluginShelves.Page" />.
/// </summary>
public sealed record PluginShelf
{
    /// <summary>The row id. The rows before and after it point at this.</summary>
    public required string Id { get; init; }

    /// <summary>The heading over the row.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// The cards. A row keeps the first <see cref="PluginPaging.MaximumRowCards" />;
    /// the rest belong behind <see cref="MoreLink" />.
    /// </summary>
    public required IReadOnlyList<PluginComponent> Items { get; init; }

    /// <summary>Where "See more" goes, as an app path, the same kind of link a card has. Null shows no link.</summary>
    public string? MoreLink { get; init; }

    /// <summary>
    /// The words on that link, drawn as written. Null writes
    /// <see cref="PluginShelves.DefaultMoreLinkText" />.
    /// </summary>
    public string? MoreLinkText { get; init; }

    /// <summary>
    /// The cursor of the row's next cards, or null when the row is complete. A
    /// row never grows past <see cref="PluginPaging.MaximumRowCards" /> cards.
    /// </summary>
    public string? NextCursor { get; init; }
}
