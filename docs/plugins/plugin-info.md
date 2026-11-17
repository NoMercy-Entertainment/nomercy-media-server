# Plugin info: where a plugin came from, and what it holds

What `GET /api/v1/dashboard/plugins` and `GET /api/v1/dashboard/plugins/{id}`
return about a plugin's source and its runtime grants, and how one grant is
taken back. This is the server half of the plugin page in
nomercy-app-web#377 (`/dashboard/plugins/manage/:id`).

Both routes, and the revoke below, are **owner-only** (`Policy = "Owner"`), the
same as the rest of the plugin info. Repository URLs and grants are never sent
to anyone else.

The change is additive. Every field the info had before keeps its name, type
and casing. A server from before this change sends neither `source` nor
`grants`, so a client can treat "field absent" as "this server cannot say".

## The two new fields

```json
{
  "id": "01JECH0000000000000000000A",
  "name": "Internet Radio",
  "...": "every existing field, unchanged",

  "source": {
    "kind": "repository",
    "repository": {
      "name": "NoMercy Marketplace",
      "url": "https://api.nomercy.tv/v1/marketplace/index.json",
      "trusted": true,
      "removed": false
    },
    "official_marketplace": true,
    "checksum_verified": true,
    "version": "1.2.0",
    "recorded_at": "2026-11-17T10:00:00Z"
  },

  "grants": [
    { "kind": "network.host", "value": "*" },
    { "kind": "capability.media.proxy", "value": "*" }
  ]
}
```

### `source`

Always present on a server with this change. Every key inside it is always
sent; a fact the server does not know is `null`, never `false`.

| key | type | meaning |
|---|---|---|
| `kind` | `"repository"` \| `"file"` \| `"unknown"` | How the plugin reached this server. |
| `repository` | object \| `null` | The repository the package was downloaded from. `null` unless `kind` is `"repository"`; a client should still allow `null` there, for an install that did not name its index. |
| `repository.name` | string \| `null` | The repository's name **when the plugin was installed**. |
| `repository.url` | string | The index URL the entry was read from. |
| `repository.trusted` | boolean | Whether this server trusts that repository **now**. Read on every request from the current repository list, so it changes the moment the owner changes trust on the repositories screen. `false` when the repository was removed or disabled. |
| `repository.removed` | boolean | The repository is no longer on this server's list. |
| `official_marketplace` | boolean \| `null` | The official NoMercy marketplace listed this entry when it was installed. `false` for an upload. |
| `checksum_verified` | boolean \| `null` | The repository published a checksum and the package matched it. `false` when the repository published none, and for an upload. |
| `version` | string \| `null` | The version that install put in place. |
| `recorded_at` | ISO 8601 UTC \| `null` | When the install or update was recorded. |

The three kinds:

- **`repository`**: installed or updated from the catalogue
  (`POST /dashboard/plugins/repositories/{pluginId}/install`), including a
  free dependency installed alongside it. A plugin two repositories list is
  recorded against the one whose entry was actually used.
- **`file`**: uploaded as a `.zip` or `.dll`
  (`POST /dashboard/plugins/install`), which needs developer mode, including an
  install for one guest (`?forUser=`). `repository` is `null`,
  `official_marketplace` and `checksum_verified` are `false`.
- **`unknown`**: installed before the server recorded sources, or put in the
  plugins folder by hand and approved. Every other key is `null`. Show it as
  "source not recorded", not as "unverified".

```json
"source": {
  "kind": "unknown",
  "repository": null,
  "official_marketplace": null,
  "checksum_verified": null,
  "version": null,
  "recorded_at": null
}
```

An update replaces the record: a plugin uploaded once and later updated from
the marketplace reads `"kind": "repository"`. An update that is waiting for the
next server start is recorded when it is accepted, so `source.version` can be
newer than the running `version` until the restart. Uninstalling drops the
record, also when the plugin's data is kept.

### `grants`

Always present; `[]` when the plugin holds nothing. Each entry is one runtime
grant the owner gave this plugin, with the same shape as the existing
`PluginGrant` in the client (`{ kind, value }`). `value` `"*"` is
`PluginGrant.Everything`, every member of that kind. Kinds seen today are
`network.host`, `library.write`, `player.source`, `capability` and
`capability.<name>`; show an unknown kind as its raw string rather than
hiding it.

## Taking one grant back

```
DELETE /api/v1/dashboard/plugins/{id}/grants?kind=network.host&value=*
```

Owner-only. Removes that one grant and leaves the plugin's other grants, its
consent and its running state as they are. Grants are checked when they are
used, so the plugin is refused from its next use; a grant that decides how the
plugin was started (such as spawning processes) applies from its next restart.

| status | when |
|---|---|
| 200 `{ "status": "ok", "message": "Grant revoked" }` | The grant was held and is gone. |
| 404 | The plugin does not hold that grant (already taken back, or never given). Refresh the info. |
| 422 | `kind` or `value` is missing. |

Match `kind` and `value` exactly as `grants` sent them, URL-encoded
(`*` may be sent as is or as `%2A`).

The routes that already existed are unchanged:

- `POST {id}/grants` with `{ kind, value, granted }` answers a **pending
  request**; `granted: false` clears the request and does not take back a grant
  already held.
- `DELETE {id}/consent` withdraws consent, takes back **every** grant and
  disables the plugin.
