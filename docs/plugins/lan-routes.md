# Serving devices on the owner's network (LAN routes)

How a plugin answers software on the owner's network that cannot sign in,
such as an HDHomeRun client in Plex, Jellyfin or Channels DVR, without
opening the rest of the plugin. The owner adds the device on the plugin's
page, the server mints a credential for it, and the device reaches only the
`GET` routes the plugin's manifest names, only from the owner's own network.

Everything here is ABI 12.10 or later (see `PluginAbi.Current`). It is all
additive: a plugin that declares no LAN routes behaves exactly as before, and
a plugin built against 12.0 to 12.9 keeps loading. A plugin that uses
`PluginControllerBase.LanCaller` or `PluginLanCaller` declares
`"targetAbi": "12.10"`.

## Declaring the routes

```json
{
  "capabilities": {
    "rest": true,
    "lan": {
      "routes": [
        "hdhr/discover.json",
        "hdhr/lineup.json",
        "hdhr/lineup_status.json",
        "hdhr/auto/{channel}"
      ]
    }
  }
}
```

Each entry is a route template relative to the plugin's own prefix, written
exactly as the plugin's controller writes it, and the plugin must have one
`[HttpGet]` action with that template. The rules (`PluginLanRoutes`) are
narrow on purpose:

| rule | why |
|---|---|
| `rest` must be on | a LAN route is one of the plugin's own REST actions |
| at most 16 routes, no duplicates | a tuner needs a handful; more is a plugin opening a surface |
| a segment is plain text of letters, digits and `. _ ~ -`, or one value written `{name}` | no catch-all, no optional part, no constraint, no encoded character, no `.` or `..` |
| no leading, trailing or double slash | the route is relative to the plugin's prefix and cannot leave it |

The server refuses to load a manifest with a route it cannot serve, and
`nomercy-plugin verify` refuses the same manifest before upload. A new LAN
route is a widening: the owner is asked again before it is served, exactly as
for a new hook or host.

## What the device calls

The owner adds a device on the plugin's page (`POST
/api/v1/dashboard/plugins/{id}/lan-devices`) and gets its address once:

```
/api/v1/plugins/{pluginId}/lan/{deviceId}/{credential}
```

Once is meant literally: the server keeps only a SHA-256 hash of the
credential (`lan-devices.json`), so it cannot show it again. A device whose
address was lost is removed and added again. A file written before hashing
keeps working; its credentials are hashed on the first read.

`GET` on that address itself answers who the device is. It existed before
the LAN routes, and it now uses the same network check as they do: a caller
on loopback, or one whose request carries a forwarding header, is refused. A
client on the same machine as the server must use the server's LAN address,
not `localhost` or `127.0.0.1`.

The device then asks for a route under that address:

```
GET /api/v1/plugins/{pluginId}/lan/{deviceId}/{credential}/hdhr/discover.json
```

The server checks, in this order, and refuses with one and the same `403`
(`PLUGIN_LAN_CREDENTIAL_INVALID`) whichever check fails, so a caller never learns
which part was wrong:

1. The device connected to the server itself, from a private address (RFC
   1918, IPv4 link-local, IPv6 unique-local or link-local), with no relay in
   between. Loopback is refused, and so is any request that carries a
   forwarding header (`X-Forwarded-*`, `Forwarded`, `X-Real-IP`, `Via`,
   `CF-*`): that is where tunnels and reverse proxies deliver callers from
   anywhere. `100.64.0.0/10` (carrier-grade NAT, and overlays such as
   Tailscale) is refused too: it is a provider's or an overlay's network,
   not the owner's house. Point the device at the server's LAN address, not
   `localhost`.
   Two setups this rules out, on purpose:
   - **A reverse proxy in front of the server on the LAN.** The server only
     believes "this came from the LAN" when nothing sits in between. A proxy
     must add a forwarding header so the server can tell its callers apart,
     and a request with one is refused here. Point LAN devices at the server
     itself, not at the proxy.
   - **A Docker published port.** Depending on the network mode, Docker can
     present its bridge gateway (such as `172.17.0.1`) as the peer for every
     caller, including callers from the internet, when it forwards through
     its userland proxy. That address is private, so the server cannot tell
     them apart, and the credential is then the only guard. Run the server
     with host networking, or make sure published ports keep the caller's
     own address, if devices use LAN routes.
2. The credential belongs to this device on this plugin and was not revoked
   (compared in fixed time).
3. The method is `GET`.
4. The address as sent is plain: no percent-encoding, no empty or dot
   segment. The path the server routed is checked as well.
5. The plugin is running, serves REST and names LAN routes.
6. The path matches exactly one route the manifest names, and the plugin has
   exactly one `GET` action with that template in its own assembly.

The action then runs through the server's MVC like any plugin route, with the
same filters. Only the sign-in step is replaced by the checks above.

Every answer on a LAN route, and on a LAN media link, carries
`Cache-Control: no-store`, written over whatever the plugin set. The
server's response cache keys an answer by its address, which holds the
credential; a kept answer would be served again without any of the checks
above, so none is ever kept.

## What the handler sees

```csharp
[HttpGet("hdhr/discover.json")]
public IActionResult Discover()
{
    if (LanCaller is not { } device)
        return NotFound();

    return Ok(new
    {
        FriendlyName = "NoMercy Live TV",
        DeviceID = device.DeviceId,
        BaseURL = device.BaseAddress.ToString().TrimEnd('/') + "/hdhr",
        LineupURL = new Uri(device.BaseAddress, "hdhr/lineup.json").ToString(),
    });
}
```

| what | on a LAN request |
|---|---|
| `LanCaller` (`PluginLanCaller`) | the device: `DeviceId`, the `Name` the owner gave it, and `BaseAddress`, the absolute address its LAN routes live under |
| `Caller` | refuses: there is no user |
| `User` | nobody: an unauthenticated principal, and no authentication result |
| `Authorization`, `Cookie`, `Proxy-Authorization`, `X-Api-Key` headers | removed |
| `token` and `access_token` query parameters | removed; other parameters stay |
| `Request.Path` | the plugin's own route, `/api/v1/plugins/{id}/hdhr/discover.json`; the credential is not in it |
| `IPluginCallerAccessor.CurrentUserId` | `Guid.Empty` |

`LanCaller` is null on every other request, so one action can serve both a
signed-in user and a device when the route is also used by the app.

`BaseAddress` carries the device's credential. Put it only in answers to this
device (HDHomeRun clients want absolute addresses in `discover.json` and the
lineup) and never in a log.

## Stream links for a device

While serving a LAN request, `context.Media.Proxy.MintAsync`,
`MintImageAsync` and `MintFileAsync` mint a link bound to the device instead
of a user. Nothing changes in the plugin's code:

```csharp
PluginMediaUrl url = await context.Media.Proxy.MintAsync(
    new() { Links = [new() { Url = channel.StreamUrl }] },
    ct
);

string lineupUrl = new Uri(LanCaller!.BaseAddress, url.Url.ToString()).ToString();
```

The minted address is `/api/v1/plugins/{id}/lan-media/{ticket}`; make it
absolute from `BaseAddress` (it is an absolute path, so only the scheme and
host are taken). A device-bound ticket:

- plays only from the owner's network, under the same check as the routes,
  and only while the plugin is running and still names LAN routes;
- is bound to that device and that plugin, and is refused on the user media
  route, just as a user's ticket is refused on this one;
- follows the user-ticket lifetime (#603): it must be played within 15
  minutes of minting, stays good while it keeps being played (2 minutes idle
  timeout), and never outlives 24 hours;
- stops the moment the owner revokes the device: the next request is
  refused, every ticket minted for the device is forgotten, and a stream the
  device has open is cut;
- mints its playlist children for the same device.

A device-bound link is a capability link: the ticket in the address is all
it takes. Any host on the owner's network that sees the address (in a
lineup it fetched, a log, or a capture of the traffic) can play it until it
idles out, the owner revokes the device, or the 24-hour cap passes. It never
plays from outside the network and never for an account.

One device may hold at most 10,000 live links per plugin at a time, as may
one account. A live window polled every few seconds stays well under that.
At the limit the device's oldest links that were never played are dropped
first; only a device whose every link is still being played is refused
(`PLUGIN_RESOURCE_CEILING`).

Because a ticket must be played within 15 minutes, a lineup that a client
caches for hours should not hold tickets. Point the lineup at a LAN route of
your own instead, such as `hdhr/auto/{channel}`, and have that action mint a
fresh link and redirect to it:

```csharp
[HttpGet("hdhr/auto/{channel}")]
public async Task<IActionResult> Tune(string channel, CancellationToken ct)
{
    PluginMediaUrl url = await context.Media.Proxy.MintAsync(RequestFor(channel), ct);

    return Redirect(new Uri(LanCaller!.BaseAddress, url.Url.ToString()).ToString());
}
```

## Security model, in short

- Every request is tied to a device the owner created and can revoke; nothing
  is opened to the LAN at large, and nothing answers outside the house.
- Only routes the owner consented to in the manifest are reachable, only with
  `GET`, and only the plugin's own actions.
- The plugin never sees a user or a user's token through a LAN request, and a
  device-bound link cannot play a user's media or another plugin's.
- A plugin with no `capabilities.lan` serves nothing here; its other routes
  keep asking for a token as before.
