# NoMercy plugin template

A plugin that loads Active on a fresh server, with nothing to fill in first.

## What is here

- `plugin.json` — what the plugin is and what it asks the owner for. The
  capabilities listed are the ones this template actually uses; the owner is
  asked about them on install and again whenever the list grows.
- `Plugin.cs` — the entry point.
- `Views/HomeView.cs` — the home page, described rather than drawn, so the host
  renders it on the web, on a phone and on a television from one description.
- `Controllers/ExampleController.cs` — one endpoint, with the capability
  declared on the action so the host refuses before the body runs.
- `settings.schema.json` — the settings page, also drawn by the host.
- `lang/` — every string the owner reads, by key. Nothing user-facing is
  written in the source.
- `tests/Plugin.Tests` — the plugin driven through `NoMercy.Plugins.Testing`,
  with the owner's grants switched on and off by the test.

## Building

```
dotnet build
```

The analyzer package is referenced, so anything that reaches around the
platform is a warning in the editor with the same sentence the server would
give you at run time.

## Releasing

Both `.github/workflows/build.yml` and `.forgejo/workflows/build.yml` build,
test and publish a zip, then write `repository.json` at a stable path. Point a
server's plugin repository list at that URL and an update shows up there.
