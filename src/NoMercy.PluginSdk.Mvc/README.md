# NoMercy.PluginSdk.Mvc

The base class a NoMercy MediaServer plugin's REST controllers inherit.

## Install

```xml
<PackageReference Include="NoMercy.PluginSdk.Mvc" Version="12.*" />
```

## A controller

```csharp
using NoMercy.PluginSdk.Mvc;

public class StationsController : PluginController
{
    [HttpGet("stations")]
    public IActionResult List() => Ok(Stations.All);
}
```

The server authenticates the request before anything reaches the plugin, and
forwards it with the caller's identity on a header. The plugin never sees a
bearer token.

Routes are mounted under the plugin's own prefix. A route that reached for a
reserved prefix is refused at load with a message naming the prefix.

Docs: https://nomercy.tv/docs/nomercy-plugins

