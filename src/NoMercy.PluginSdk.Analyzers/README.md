# NoMercy.PluginSdk.Analyzers

Editor warnings worded the way the server words its refusals, so a plugin author
reads one sentence rather than two.

## Install

```xml
<PackageReference Include="NoMercy.PluginSdk.Analyzers" Version="12.*" PrivateAssets="all" />
```

`PrivateAssets="all"` keeps the analyzer out of the published plugin. It is a
build-time tool, not a dependency.

## What it catches

A capability used but never declared, a raw `HttpClient` where the granted one
belongs, a token in a media URL, a component name that no client has. Each
warning carries the same code the server would refuse with, so the fix in the
editor is the fix in production.

Docs: https://nomercy.tv/docs/nomercy-plugins

