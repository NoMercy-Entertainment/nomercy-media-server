# NoMercy.Plugins.Testing

Drive a NoMercy MediaServer plugin through a fake context, with the owner grants
a test switches on and off.

## Install

```xml
<PackageReference Include="NoMercy.Plugins.Testing" Version="11.*" />
```

## A test

```csharp
using NoMercy.Plugins.Testing;

[Fact]
public async Task TheStationListNeedsTheNetworkGrant()
{
    FakePluginContext context = new();
    RadioPlugin plugin = new();
    plugin.Initialize(context);

    // No grant: the call refuses the way the real server refuses.
    await Assert.ThrowsAsync<PluginRefusedException>(() => plugin.RefreshAsync());

    context.Grant("network.dial", "radio.example");

    await plugin.RefreshAsync();
}
```

The fakes refuse with the same `PluginRefusal` the server raises, so a test
that passes here is a plugin that will not surprise its owner with a refusal on
first run.

Docs: https://nomercy.tv/docs/nomercy-plugins

