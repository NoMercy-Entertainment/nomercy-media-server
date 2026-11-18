# Shelves and paging

This page is the contract for plugin shelves and plugin paging. The SDK
writes it, the server enforces it, and the clients build against it. There is
no second paging contract.

It is ABI 12.11 (see `PluginAbi.Current`). Everything here is additive. A
plugin built against 12.0 to 12.10 keeps loading and keeps drawing what it drew.

## The names

A plugin uses the app's own components, under their own names. Nothing new is
named.

| What | Component | SDK builder |
|---|---|---|
| A grid of cards | `NMGrid` | `PluginShelves.Grid` |
| One card (artist, album, station) | `NMMusicCard` | `PluginShelves.MusicCard` |
| One home-page row | `NMCarousel` | `PluginShelves.Row` |
| A page of rows (the LOLOMO) | `NMLolomo` | `PluginShelves.Page` |
| Page numbers | `NMPagination` | `PluginShelves.Pagination` |
| The next page | action `loadPage` | `PluginActionIntent.LoadPage` |

For a plugin, `NMCarousel` is the app's home-page row: a title, "See more",
and the links to the row before and after it. It is not the design system's
carousel. `NMLolomo` is the exact shape the server already sends to the TV app.

## The rules in short

- Every shelf and every card carries its `id` twice: on the envelope and on
  `props`. Without the second one the whole shelf fails to decode and draws
  nothing. The analyzer rule NMP0017 catches this at build time.
- A grid holds at most 500 items in one page.
- A row holds at most 36 cards. More cards go behind "See more".
- A page holds at most 36 rows. More rows arrive as a next page.
- The server holds every plugin to these caps, whatever the plugin sends.
- A link (`more_link`, a card's `data.link`) is an app path, such as
  `/music/plugins/{pluginId}/genres/rock`. `PluginKind.PrefixFor` gives the
  start of it.

The numbers are in `PluginPaging`. They are the home page's own caps
(`UiLimits`), and a test keeps them equal.

## The shelves

### NMGrid

```json
{
  "id": "all-stations",
  "component": "NMGrid",
  "props": {
    "id": "all-stations",
    "items": [ { "id": "station-1", "component": "NMMusicCard", "props": { } } ],
    "loadMore": { "type": "loadPage", "payload": { } }
  }
}
```

- `items` is always there. An empty page is `"items": []`.
- `loadMore` is there only when there is a next page. See
  [The loadPage action](#the-loadpage-action).

### NMMusicCard

```json
{
  "id": "station-1",
  "component": "NMMusicCard",
  "props": {
    "id": "station-1",
    "data": {
      "id": "station-1",
      "name": "Groove Salad",
      "link": "/music/plugins/{pluginId}/stations/1",
      "cover": "data:image/png;base64,...",
      "type": "artist",
      "description": "Ambient · US"
    },
    "contextMenuItems": []
  }
}
```

- `description` is the second line. If it is null, the card shows its `type`
  there (for example "Artist"). The SDK makes the plugin choose: the field is
  required, even when it is null.
- `type` sets the shape of the picture: `artist` is a framed square, `album`
  is a record sleeve.
- The card is a link to `data.link`. It does not run an action.
- `cover` and `type` are written as `null` when not set.

### NMCarousel (one row)

```json
{
  "id": "genre-rock",
  "component": "NMCarousel",
  "props": {
    "id": "genre-rock",
    "next_id": "genre-jazz",
    "previous_id": "genre-pop",
    "title": "Rock",
    "more_link": "/music/plugins/{pluginId}/genres/rock",
    "more_link_text": "See more",
    "index": 1,
    "items": [ { "id": "rock-1", "component": "NMMusicCard", "props": { } } ],
    "loadMore": { "type": "loadPage", "payload": { } }
  }
}
```

- These are the keys the server's own home rows carry (`ContainerProps`), in
  the same snake case. A test compares the two.
- `next_id` and `previous_id` are left out when there is no row there, as the
  server leaves them out. A row on its own has neither.
- `more_link` and `more_link_text` are written together, or not at all.
  `more_link_text` is drawn as written. The SDK default is `See more`.
- `index` is the row's place on the page, from 0. The app's row needs it. The
  home page's renderer adds it; a plugin row has no renderer, so the SDK
  writes it.
- `loadMore` is rare on a row. A row never holds more than 36 cards.

### NMLolomo (a page of rows)

```json
{
  "id": "radio-home",
  "component": "NMLolomo",
  "props": {
    "id": "radio-home",
    "hero": "focus",
    "items": [
      { "id": "genre-pop",  "component": "NMCarousel", "props": { "id": "genre-pop",  "next_id": "genre-rock", "title": "Pop",  "index": 0, "items": [] } },
      { "id": "genre-rock", "component": "NMCarousel", "props": { "id": "genre-rock", "next_id": "genre-jazz", "previous_id": "genre-pop", "title": "Rock", "index": 1, "items": [] } },
      { "id": "genre-jazz", "component": "NMCarousel", "props": { "id": "genre-jazz", "previous_id": "genre-rock", "title": "Jazz", "index": 2, "items": [] } }
    ],
    "loadMore": { "type": "loadPage", "payload": { } }
  }
}
```

(The rows' `items` are shortened to `[]` here. A sent row always has cards.)

- The same shape the server sends the TV app (`LolomoComponentBuilder`).
- `items` holds only `NMCarousel` rows, flat, in display order.
- A row with no cards is left out.
- The first row has no `previous_id`. The last row has no `next_id`.
- `hero` is `focus` (the hero shows the focused card) or `none`.
- `loadMore`, when there, adds more rows.

### NMPagination

```json
{
  "id": "pages",
  "component": "NMPagination",
  "props": {
    "currentPage": 2,
    "totalPages": 40,
    "action": {
      "type": "loadPage",
      "payload": { "route": "/all", "target": "pages", "cursor": null, "limit": 36, "mode": "replace", "query": {} },
      "access": "Shared"
    }
  }
}
```

When the person picks a page, the client puts that page number, as a string,
in `payload.cursor`, then runs the action. The plugin reads it back with
`request.Page.Offset` (or `request.Page.Cursor`).

## The loadPage action

```json
{
  "type": "loadPage",
  "payload": {
    "route": "/stations",
    "target": "all-stations",
    "cursor": "36",
    "limit": 36,
    "mode": "append",
    "query": { "country": "NL" }
  },
  "access": "Shared"
}
```

| Key | Meaning |
|---|---|
| `route` | The plugin route to ask. The same view the shelf came from. |
| `target` | The id of the shelf to fill. |
| `cursor` | The plugin's own cursor for the next page. A string the client never reads. `null` for page numbers, where the client fills it in. |
| `limit` | How many items to ask for. Always 1 to 500. |
| `mode` | `append` (add to the shelf) or `replace` (draw the whole view at that page). Anything else is `append`. |
| `query` | The rest of the view's own query, such as a search term. Send each pair back as it is. It never holds a key the contract owns. |

`access` is the action's usual access (`Shared` or `Owner`).

### Asking for a page

A page is asked for on the normal view endpoint, with three extra query keys:

| Query key | Value |
|---|---|
| `cursor` | `payload.cursor`, left out when null |
| `limit` | `payload.limit` |
| `append` | `payload.target`. Only in `append` mode. |

Append mode:

```
GET /api/v1/plugins/{pluginId}/view?route=/stations&surface=web&input=pointer&features=paging&append=all-stations&cursor=36&limit=36&country=NL
```

Replace mode (page 3 picked):

```
GET /api/v1/plugins/{pluginId}/view?route=/all&surface=web&input=pointer&features=paging&cursor=3&limit=36
```

The keys `route`, `surface`, `input`, `features`, `cursor`, `limit` and
`append` belong to the contract (`PluginPaging.ReservedQueryKeys`). A plugin
never uses them for anything else.

### The answer to an append

The normal view answer, holding one component: the target shelf, with the
same id, carrying only the next items.

```json
{
  "data": {
    "components": [
      {
        "id": "all-stations",
        "component": "NMGrid",
        "props": {
          "id": "all-stations",
          "items": [ { "id": "station-37", "component": "NMMusicCard", "props": { } } ],
          "loadMore": { "type": "loadPage", "payload": { "route": "/stations", "target": "all-stations", "cursor": "72", "limit": 36, "mode": "append", "query": { "country": "NL" } }, "access": "Shared" }
        }
      }
    ],
    "layout": "standard",
    "refreshInterval": 0
  }
}
```

The client then:

1. Adds `items` to the end of the shelf it already shows.
2. Takes the new `loadMore`. When the answer has no `loadMore`, there are no
   more pages.
3. When `components` is empty, there are no more pages either.

For an `NMLolomo`, the new items are rows. The client also links them in: the
old last row gets `next_id` = the first new row, the first new row gets
`previous_id` = the old last row, and `index` counts on from the old rows.

For an `NMCarousel`, the client stops at 36 cards and drops `loadMore`.

The plugin does not need a special answer. It may build its whole view as it
always does, for the cursor it was given. The server finds the shelf named in
`append` and sends only that one.

### The answer to a replace

The whole view, for that page. The client draws it instead of the current
one. It keeps sending the same `cursor` and `limit` on every reload of that
screen (a `refreshView`, a push, a `refreshInterval`) until the person leaves
the screen.

## What the plugin writes

```csharp
public Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct)
{
    PluginPageRequest page = request.Page;
    List<Station> slice = catalog.Skip(page.Offset).Take(page.Limit).ToList();
    string? next = page.Offset + slice.Count < catalog.Count
        ? (page.Offset + slice.Count).ToString(CultureInfo.InvariantCulture)
        : null;

    return Task.FromResult(PluginViews.Declarative(
        PluginShelves.Grid("all-stations", slice.Select(Tile), request, next)));
}
```

- `request.Page.Cursor` is the cursor, or null on the first page.
- `request.Page.Offset` is the cursor as a number, or 0.
- `request.Page.Limit` is always 1 to 500. Unset, it is 36.
- `request.Page.Append` names the shelf, on an append.
- `request.Features.Supports(PluginClientFeatures.Paging)` says whether the
  client can run `loadPage`. A plugin that used to send one long list can
  keep sending it to a client without paging.

## Client features

A client lists what it can do in one query value: `features=paging,rows`.
Names are separated by commas and keep their case. A plugin reads them with
`request.Features.Supports(...)`.

| Word | Constant | Means |
|---|---|---|
| `paging` | `PluginClientFeatures.Paging` | The client runs `loadPage` and reads a shelf's `loadMore`. |
| `rows` | `PluginClientFeatures.Rows` | The client draws a plugin's `NMCarousel` as its own home-page row, and draws `NMLolomo`. |

The two are separate on purpose. A client can page before it draws the app's
row. Send `NMCarousel` as the home row, and `NMLolomo`, only to a client that
sends `rows`. To any other client, send the old shape, for example a heading
above the design system's carousel. `rows` is in ABI 12.12 and later.

## What the server enforces

On every view answer, before it is sent (`PluginShelfCaps`):

- An `NMGrid` keeps its first 500 items.
- An `NMCarousel` keeps its first 36 cards.
- An `NMLolomo` keeps its first 36 rows.
- An append keeps only the target shelf, and only `limit` of its items.

The server reads a shelf's children in three shapes: SDK nodes
(`PluginComponent`), JSON objects (what a `Design` record becomes), and
dictionaries written by hand with `id`, `component` and `props.items`. Keys
match without case. Each shape is capped, and each can be the target of an
append. Any other object in a list still counts against the cap, but is not
looked into. The server never changes the plugin's own objects; it sends
copies.

Items past a cap are left out, and the server logs a warning. The plugin's
next cursor is sent as the plugin wrote it, so a plugin that goes over the cap
skips the items it went over by. Keep to the limit.

The server also refuses a view that names a component a plugin may never
draw, at any depth (`NmPluginForbiddenComponents`, today `NMServerComponent`).
The answer is a 422 with the refusal `PLUGIN_VIEW_COMPONENT_FORBIDDEN`.

## What an older app shows

An older app is one without app-web #376.

| Sent | What an older app shows |
|---|---|
| `NMGrid`, `NMMusicCard` | The same as today. They already work. |
| `loadMore` on a shelf | Nothing. The key is ignored, and only the first page shows. |
| `NMCarousel` | The design system's carousel: the cards in one sideways row, with no title, no "See more" and no skip links. |
| `NMLolomo` | A one-line note, "This app cannot show NMLolomo", in place of the page. The rest of the screen still works. |
| `NMPagination` | The page numbers. Pressing one does nothing. |
| a `loadPage` action | Nothing. An unknown action type is a no-op on the web and on Compose. |
| `features` | An older app sends none, so `request.Features` is empty. |

So nothing breaks, and nothing harmful runs. A plugin that must look right on
an older app can check `request.Features` and send its old view: `rows` for
the home row and `NMLolomo`, `paging` for `loadPage`.

## What the client builds (app-web #376 and Compose)

1. Draw `NMCarousel` from a plugin as the app's home-page row, with the
   props above.
2. Add `NMLolomo` to the components a plugin may draw, with the shape above.
3. Send `features` on every view request, GET and POST: `paging` once the
   client runs `loadPage`, and `rows` once it draws `NMCarousel` as the home
   row and draws `NMLolomo` (steps 1 and 2). Together: `features=paging,rows`.
4. Run `loadPage` in both modes, as above.
5. Read `loadMore` on `NMGrid`, `NMCarousel` and `NMLolomo`. Whether that is
   a "Load more" button or loading on scroll is the client's choice.
6. On `NMPagination`, when the person picks a page, put the page number in
   `payload.cursor` and run the action.
7. After an append to an `NMLolomo`, link the rows and count `index` on.
8. Stop a row at 36 cards.
9. Treat a 422 refusal as a failed view.

Open for the client to decide:

- A full reload (`refreshView`, a push) draws the first page again, so
  appended pages are gone. A client may ask the reload with `limit` set to the
  number of items it holds (at most 500) to keep the person's place.
- Today the browser's own query string is not sent with a plugin view
  request, so a plugin can only see paging through `loadPage`.
