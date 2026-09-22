# NoMercy.PluginSdk.Abstractions

The contract a NoMercy MediaServer plugin is written against: `IPlugin`, the
entry-point interfaces, the facades on `IPluginContext`, the capability
vocabulary and the refusal shape.

## Install

```xml
<PackageReference Include="NoMercy.PluginSdk.Abstractions" Version="12.*" />
```

Pin the major, never the minor. The contract adds members in a minor version,
so a pinned minor forces every plugin to re-pin on a release that changed
nothing for it.

## A plugin

```csharp
using NoMercy.PluginSdk.Abstractions;

public class RadioPlugin : IPlugin
{
    public Ulid Id { get; } = Ulid.Parse("01JB0000000000000000000000");
    public string Name => "Internet Radio";
    public string Description => "Stations, in the library.";
    public Version Version => new(1, 0);

    public void Initialize(IPluginContext context)
    {
        context.Logger.LogInformation("Ready.");
    }

    public void Dispose() { }
}
```

## Capabilities

Every facade is gated. A plugin declares what it needs in `plugin.json`, the
owner consents on the permissions page, and a call without either is refused
with a `PluginRefusal` that names what happened, why, and the one line to
change:

```csharp
// Refused unless the manifest declares storage.path and the owner granted
// that folder.
IPluginStorageScope movies = await context.Storage.PathAsync("movies");
```

## Isolation

A plugin runs in its own process, in the platform's sandbox: a Windows job
object, a Linux cgroup v2 slice, or a macOS sandbox profile. The facades behave
identically in either runtime, so nothing here changes when a server switches.

Docs: https://nomercy.tv/docs/nomercy-plugins

