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
/// The words and the limits of the paging contract, written down once.
/// <para>
/// A view asks for one page of a shelf through its query string, and a
/// <see cref="PluginActionType.LoadPage" /> action asks for the next one. The
/// client, the SDK and the host all read these names, so a typo here is a page
/// nobody can reach. The contract is <see cref="PluginDocs.ShelvesAndPaging" />.
/// </para>
/// </summary>
public static class PluginPaging
{
    /// <summary>The query key that carries the plugin's own cursor for the page it wants.</summary>
    public const string CursorKey = "cursor";

    /// <summary>The query key that carries how many items the client wants.</summary>
    public const string LimitKey = "limit";

    /// <summary>
    /// The query key that names the shelf a page is for. Set only when the client
    /// appends to a shelf it already shows.
    /// </summary>
    public const string AppendKey = "append";

    /// <summary>
    /// How many items a page holds when the client does not say.
    /// <para>
    /// The home page's row length. It also divides evenly into two, three, four,
    /// six and nine columns, so a grid page ends on a full line on most screens.
    /// </para>
    /// </summary>
    public const int DefaultPageSize = 36;

    /// <summary>
    /// The most items one page may hold. A larger limit is read as this one, and
    /// the host trims a grid that sends more. The same cap the home page puts on
    /// one page of a library.
    /// </summary>
    public const int MaximumPageSize = 500;

    /// <summary>
    /// The most cards in one row, the same cap the home page puts on its own rows
    /// (<c>UiLimits.MaximumCardsInCarousel</c>). A row that has more says so with
    /// a "See more" link to a grid.
    /// </summary>
    public const int MaximumRowCards = 36;

    /// <summary>The most rows in one shelf page. More rows arrive through a next cursor.</summary>
    public const int MaximumPageRows = 36;

    /// <summary>A <see cref="PluginActionType.LoadPage" /> mode: add the page to the end of the shelf.</summary>
    public const string Append = "append";

    /// <summary>
    /// A <see cref="PluginActionType.LoadPage" /> mode: ask for the whole view at that
    /// cursor and draw it instead of the current one. What a page-number control uses.
    /// </summary>
    public const string Replace = "replace";

    /// <summary>
    /// Query keys a client or the host adds to every view request. A load-more
    /// action carries the rest of the query along, and never these.
    /// </summary>
    public static IReadOnlySet<string> ReservedQueryKeys { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "route",
            "surface",
            "input",
            PluginClientFeatures.QueryKey,
            CursorKey,
            LimitKey,
            AppendKey,
        };
}
