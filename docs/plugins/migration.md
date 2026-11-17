# Moving a plugin to SDK 12

This page is for a plugin built against an SDK older than 12. A refusal
sends you here when your plugin calls a member that no longer exists.

SDK 12 renamed the packages and namespaces. Just before that, the host
container and the raw event bus were taken off `IPluginContext`. A plugin
built against 11 resolves none of its SDK types on a 12 server, so it has to
be rebuilt.

## 1. Rename the packages and namespaces

Every SDK package moved from `NoMercy.Plugins.*` to `NoMercy.PluginSdk.*`.
The namespaces moved with them.

| Before (11)                    | Now (12)                         |
| ------------------------------ | -------------------------------- |
| `NoMercy.Plugins.Abstractions` | `NoMercy.PluginSdk.Abstractions` |
| `NoMercy.Plugins.Mvc`          | `NoMercy.PluginSdk.Mvc`          |
| `NoMercy.Plugins.Testing`      | `NoMercy.PluginSdk.Testing`      |
| `NoMercy.Plugins.Analyzers`    | `NoMercy.PluginSdk.Analyzers`    |

In the project file:

```xml
<PackageReference Include="NoMercy.PluginSdk.Abstractions" Version="12.*" />
```

In code:

```csharp
using NoMercy.PluginSdk.Abstractions;
```

Pin the major (`12.*`), not the minor. See [versioning](versioning.md).

## 2. Set targetAbi to 12

In `plugin.json`, set `targetAbi` to the ABI you build against. Today that is
the value of `PluginAbi.Current`:

```json
{
  "targetAbi": "12.10"
}
```

A server refuses a plugin whose `targetAbi` major is below 12. It does not
give 11 the usual one-major grace, because no type from 11 exists any more.

## 3. Replace EventBus and Services

Three members left `IPluginContext`:

| Before                                  | Now (12)                                  |
| --------------------------------------- | ----------------------------------------- |
| `context.EventBus` (`IEventBus`)        | `context.Events` (`IPluginEvents`)        |
| `context.Services` (`IServiceProvider`) | the facade on `IPluginContext` you needed |
| `context.System` (`IPluginSystem`)      | the facade on `IPluginContext` you needed |

`context.Events` has two members:

- `Subscribe<T>(topic, handler)` listens to a server topic. The topic names
  are in `PluginTopics`.
- `PublishAsync(name, payload)` sends one of the plugin's own events.

The raw bus carried every event the server raised, also ones about users the
plugin has no grant for. The topic list does not.

For `Services`, there is no container to ask any more. Each thing a plugin
may reach is a facade on `IPluginContext`, such as `context.Metadata`,
`context.Storage` or `context.Net`. Each facade is behind a capability that
the manifest declares and the owner grants. The full list is on the
[capabilities page](https://docs.nomercy.tv/nomercy-plugins/capabilities).

## What a plugin that was not moved sees

- It is refused at install if its `targetAbi` is below 12.
- If it does load, the first call to a member that is gone is refused with
  `PLUGIN_HOST_SERVICES_REMOVED`. The message names the missing member and the
  SDK version to rebuild against.

## Installing your own build from a file

A server does not install a plugin from a `.zip` or `.dll` file by default,
because it cannot check who wrote the file. The refusal is
`PLUGIN_SIDELOAD_DISABLED`.

To install your own build:

1. In the dashboard, open **Advanced**, then **Plugins**.
2. Turn on developer mode: switch on **Install plugins from a file**, and
   read the warning under it. Only the server owner sees this switch, and
   only on the desktop layout.
3. Install the file again.

The same switch is `POST /api/v1/dashboard/plugins/developer-mode` with
`{ "enabled": true }`.

## Older plugin.json files keep loading

You do not have to rewrite `plugin.json`. Every field added after a manifest
was written is optional and has a default, so an older manifest still parses.
Only `targetAbi` needs to change. The fields are listed in
[the manifest page](manifest.md).
