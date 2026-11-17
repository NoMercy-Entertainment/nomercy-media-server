# Plugin versioning (the ABI)

The plugin contract has a version of its own, the ABI. It is written as
`major.minor`, for example `12.10`. The server's current ABI is
`PluginAbi.Current`. The SDK packages carry the same major and minor
(`NoMercy.PluginSdk.Abstractions` 12.10.0 speaks ABI 12.10).

The ABI is not the server's own version. A plugin reads the server's version
from `context.Server.Version`.

## What a minor and a major mean

- **A minor adds.** New members, new types, new optional manifest fields.
  Nothing a plugin already uses changes shape. A new member on an interface
  is a default member, so a plugin or a test double that implements the
  interface keeps compiling.
- **A major removes or renames.** Members a plugin used can be gone.

Each minor and what it added is listed in the comment above
`PluginAbi.Current` in `src/NoMercy.PluginSdk.Abstractions/PluginAbi.cs`.

## targetAbi in plugin.json

`targetAbi` is the ABI the plugin was built against. It is also the oldest
server ABI the plugin can run on. The server checks it at install and at load
(`PluginAbi.IsCompatible`):

| targetAbi                                        | Result                      |
| ------------------------------------------------ | --------------------------- |
| Not set                                          | Loads                       |
| Not a version, such as `"twelve"`                | Refused                     |
| Major below `PluginAbi.Oldest`                   | Refused                     |
| An older major that is not below `Oldest`        | Loads                       |
| Same major, minor at or below the server's minor | Loads                       |
| Same major, minor above the server's minor       | Refused: update the server  |
| A newer major                                    | Refused: update the server  |

The usual grace is one major: a plugin built against the previous major keeps
loading, because a major mostly removes members it may never have called.

ABI 12 has no grace. `PluginAbi.Oldest` is 12.0, so a plugin built against 11
is refused. ABI 12 renamed the SDK assembly and every namespace, and nothing
built against 11 resolves a single type. See [migration](migration.md).

## Which targetAbi to set

Set the lowest minor whose members your plugin uses. A plugin that uses
nothing added after 12.0 can say `"12.0"` and run on every 12 server. A
plugin that uses a member from 12.10 says `"12.10"`.

`nomercy-plugin verify` checks the value. It refuses a value that is not a
version or that this SDK cannot load. It warns when the major is older than
the current one, because the plugin cannot see anything added since.

## A facade that is not on this server

A plugin can be built against a newer contract than the server it runs on.
When it calls a facade the server does not offer, the call is refused with
`PLUGIN_CONTRACT_VERSION_MISMATCH`.

Fix it one of two ways:

- Update the server.
- Set a lower `targetAbi`, and check `context.Server.Version` before you use
  the newer facade.

## Package versions

Reference the SDK by major only:

```xml
<PackageReference Include="NoMercy.PluginSdk.Abstractions" Version="12.*" />
```

A minor only adds, so pinning one makes every plugin re-pin on a release that
changed nothing for it.
