# NoMercy.Design

The NoMercy design system's component contract, for servers and plugins.

A plugin's page is data, never markup. It names components the clients already
have, and every client draws it natively: the web app, the phone, the TV and the
cast receiver.

## There is no package for this

It ships as an assembly inside `NoMercy.PluginSdk.Abstractions`. A plugin author
installs that one package and gets this with it:

```xml
<PackageReference Include="NoMercy.PluginSdk.Abstractions" Version="12.*" />
```

## Drawing a page

```csharp
public Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct) =>
    Task.FromResult(new PluginView
    {
        Components =
        [
            Nm.Container(
                Nm.Heading("Stations"),
                Nm.Button("Refresh", action: "refresh")
            ),
        ],
    });
```

Use the generic components and the theme steps (`theme-1` to `theme-12`) plus
`danger`, `info` and the defaults. A plugin does not ship its own component: one
that did would exist on the web app and be missing on the television.
