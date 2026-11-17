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

using NoMercy.Design;

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Builds the app's own shelves: the grid, the music card, the home-page row
/// and the page of rows. Each one writes exactly the props the app's screens
/// are drawn from, so a plugin's shelf is the same shelf the app shows.
/// <para>
/// The keys are the app's, not new ones. A row is the home page's
/// <c>NMCarousel</c> with <c>title</c>, <c>more_link</c>, <c>previous_id</c>
/// and <c>next_id</c>; a page is the TV app's <c>NMLolomo</c>. The only key
/// added is <c>loadMore</c>, which an older client ignores.
/// The contract is <see cref="PluginDocs.ShelvesAndPaging" />.
/// </para>
/// </summary>
public static class PluginShelves
{
    /// <summary>The hero of a page shows the focused card. The server's own default.</summary>
    public const string HeroFocus = "focus";

    /// <summary>The page has no hero.</summary>
    public const string HeroNone = "none";

    /// <summary>The words on a row's link when the plugin gives none. The app's own default.</summary>
    public const string DefaultMoreLinkText = "See more";

    private const string LoadMoreKey = "loadMore";

    /// <summary>
    /// The app's grid, the one its libraries are laid out with. It keeps the
    /// first <see cref="PluginPaging.MaximumPageSize" /> items.
    /// <para>
    /// Put app cards in it, such as <see cref="MusicCard" />. Today's web app
    /// draws a grid's items with the app's own components only.
    /// </para>
    /// </summary>
    public static PluginComponent Grid(string id, IEnumerable<PluginComponent> items) =>
        Grid(id, items, request: null, nextCursor: null);

    /// <summary>
    /// The same grid, with the next page behind a load-more action when
    /// <paramref name="nextCursor" /> is set. The action asks the view in
    /// <paramref name="request" /> again, with the same query.
    /// </summary>
    public static PluginComponent Grid(
        string id,
        IEnumerable<PluginComponent> items,
        PluginViewRequest? request,
        string? nextCursor
    )
    {
        Dictionary<string, object?> props = new()
        {
            // On the props as well as the envelope: the app's grid reads it
            // there, and without it the whole shelf fails to decode and draws
            // nothing.
            ["id"] = id,
            // Always present, so an empty page is an empty grid rather than a
            // grid missing a required list.
            ["items"] = Array.Empty<PluginComponent>(),
        };
        AddLoadMore(props, id, request, nextCursor);

        return new()
        {
            Id = id,
            Component = NmAppComponents.Grid,
            Props = props,
            Items = [.. items.Take(PluginPaging.MaximumPageSize)],
        };
    }

    /// <summary>One tile, drawn exactly as the app draws an artist or an album.</summary>
    public static PluginComponent MusicCard(PluginShelfCard card) =>
        new()
        {
            Id = card.Id,
            Component = NmAppComponents.MusicCard,
            Props = new()
            {
                // On the props as well as inside data. A card without it fails
                // the whole shelf's decode, and the grid then draws nothing.
                ["id"] = card.Id,
                ["data"] = new Dictionary<string, object?>
                {
                    ["id"] = card.Id,
                    ["name"] = card.Name,
                    ["link"] = card.Link,
                    ["cover"] = card.Cover,
                    ["type"] = card.Type,
                    ["description"] = card.Description,
                },
                ["contextMenuItems"] = card.ContextMenu,
            },
        };

    /// <summary>
    /// One home-page row on its own. Its cards are capped at
    /// <see cref="PluginPaging.MaximumRowCards" />. <paramref name="request" />
    /// is needed only when the row has a next cursor.
    /// </summary>
    public static PluginComponent Row(PluginShelf shelf, PluginViewRequest? request = null) =>
        Row(shelf, request, index: 0, previousId: null, nextId: null);

    /// <summary>
    /// A page of home-page rows, chained the way the server chains its own:
    /// rows with no cards are left out, each row names the one before and after
    /// it, the first has no previous row and the last has no next one.
    /// <para>
    /// It keeps the first <see cref="PluginPaging.MaximumPageRows" /> rows. More
    /// rows arrive through <paramref name="nextCursor" />, which needs
    /// <paramref name="request" />.
    /// </para>
    /// </summary>
    public static PluginComponent Page(
        string id,
        IEnumerable<PluginShelf> shelves,
        PluginViewRequest? request = null,
        string? nextCursor = null,
        string hero = HeroFocus
    )
    {
        List<PluginShelf> kept =
        [
            .. shelves.Where(shelf => shelf.Items.Count > 0).Take(PluginPaging.MaximumPageRows),
        ];

        Dictionary<string, object?> props = new()
        {
            ["id"] = id,
            // Anything but "none" reads as focus, as it does on the server.
            ["hero"] = hero == HeroNone ? HeroNone : HeroFocus,
            ["items"] = Array.Empty<PluginComponent>(),
        };
        AddLoadMore(props, id, request, nextCursor);

        return new()
        {
            Id = id,
            Component = NmAppComponents.Lolomo,
            Props = props,
            Items =
            [
                .. kept.Select(
                    (PluginShelf shelf, int index) =>
                        Row(
                            shelf,
                            request,
                            index,
                            index == 0 ? null : kept[index - 1].Id,
                            index == kept.Count - 1 ? null : kept[index + 1].Id
                        )
                ),
            ],
        };
    }

    /// <summary>
    /// Page numbers under a shelf. Choosing one asks for the whole view at
    /// that page: the client puts the page number in the action's
    /// <c>cursor</c> and draws the view it gets back. A plugin reads it with
    /// <see cref="PluginPageRequest.Offset" />.
    /// </summary>
    public static PluginComponent Pagination(
        string id,
        int currentPage,
        int totalPages,
        PluginViewRequest request
    )
    {
        int total = Math.Max(1, totalPages);

        return new()
        {
            Id = id,
            Component = "NMPagination",
            Props = new()
            {
                ["currentPage"] = Math.Clamp(currentPage, 1, total),
                ["totalPages"] = total,
            },
            Action = PluginActionIntent.LoadPage(
                request.Route,
                id,
                cursor: null,
                request.Page.Limit,
                PluginPaging.Replace,
                request.Query
            ),
        };
    }

    /// <summary>
    /// The row with the keys the home page's own rows carry. Null keys are left
    /// out, as the server leaves them out, so a first row has no previous_id.
    /// </summary>
    private static PluginComponent Row(
        PluginShelf shelf,
        PluginViewRequest? request,
        int index,
        string? previousId,
        string? nextId
    )
    {
        Dictionary<string, object?> props = new() { ["id"] = shelf.Id };

        if (nextId is not null)
            props["next_id"] = nextId;

        if (previousId is not null)
            props["previous_id"] = previousId;

        props["title"] = shelf.Title;

        if (shelf.MoreLink is not null)
        {
            props["more_link"] = shelf.MoreLink;
            // Always beside the link, as the server's own rows write it.
            props["more_link_text"] = shelf.MoreLinkText ?? DefaultMoreLinkText;
        }

        // The row's place on the page. The app's row takes it to lay itself
        // out; the home page's renderer adds it, and a plugin's row has no
        // renderer to do that.
        props["index"] = index;
        props["items"] = Array.Empty<PluginComponent>();

        AddLoadMore(props, shelf.Id, request, shelf.NextCursor);

        return new()
        {
            Id = shelf.Id,
            Component = NmAppComponents.Carousel,
            Props = props,
            Items = [.. shelf.Items.Take(PluginPaging.MaximumRowCards)],
        };
    }

    private static void AddLoadMore(
        Dictionary<string, object?> props,
        string id,
        PluginViewRequest? request,
        string? nextCursor
    )
    {
        if (nextCursor is null)
            return;

        // Thrown rather than skipped: a next page nobody can ask for is a shelf
        // that silently stops, and the author would not find out why.
        if (request is null)
            throw new ArgumentNullException(
                nameof(request),
                $"Shelf {id} has a next cursor, so it needs the view request to ask for the next page from. Docs: {PluginDocs.ShelvesAndPaging}"
            );

        props[LoadMoreKey] = PluginActionIntent.LoadPage(
            request.Route,
            id,
            nextCursor,
            request.Page.Limit,
            PluginPaging.Append,
            request.Query
        );
    }
}
