# NoMercy.Events

The event contract shared between NoMercy MediaServer and its plugins.
`PluginMessageEvent` is the envelope a plugin publishes and subscribes through.

## There is no package for this

It ships as an assembly inside `NoMercy.PluginSdk.Abstractions`. A plugin author
installs that one package and gets this with it:

```xml
<PackageReference Include="NoMercy.PluginSdk.Abstractions" Version="12.*" />
```

One package rather than three that have to be held on the same version by hand.
Nothing ever referenced this alone.
