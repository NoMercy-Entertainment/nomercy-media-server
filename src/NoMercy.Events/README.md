# NoMercy.Plugins.Events

The event contract shared between NoMercy MediaServer and its plugins.

`PluginMessageEvent` is the envelope a plugin publishes and subscribes through.
It travels with `NoMercy.Plugins.Abstractions` rather than separately: a plugin
and the server must agree on one type, not two that look alike.

## Install

```xml
<PackageReference Include="NoMercy.Plugins.Events" Version="11.*" />
```

Referencing `NoMercy.Plugins.Abstractions` brings this with it. Add it directly
only when a project needs the envelope and nothing else.

Docs: https://nomercy.tv/docs/nomercy-plugins

