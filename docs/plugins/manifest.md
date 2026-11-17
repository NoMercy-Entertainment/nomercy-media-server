# The plugin manifest (plugin.json)

Every plugin has a `plugin.json` in the root of its folder, next to the
assembly it names. The server reads it before it loads any code, so the
owner can see what the plugin is and what it asks for first.

`nomercy-plugin new` writes a starting one. `nomercy-plugin verify` checks it,
run from the folder that holds `plugin.json`.

The server reads the file leniently: names are not case sensitive, and
comments and trailing commas are allowed. The shape is the `PluginManifest`
record in `NoMercy.PluginSdk.Abstractions`.

## Fields

| Field          | Needed | What it is                                                        |
| -------------- | ------ | ----------------------------------------------------------------- |
| `id`           | yes    | The plugin's id. Never empty.                                     |
| `name`         | yes    | The name the owner sees.                                          |
| `description`  | yes    | One line about what the plugin does.                              |
| `version`      | yes    | The plugin's own version, such as `1.0.0`.                        |
| `assembly`     | yes    | The plugin's `.dll` file name. A plain file name, never a path.   |
| `targetAbi`    | no     | The ABI the plugin was built against. See [versioning](versioning.md). |
| `tier`         | no     | `free` or `paid`. Default `free`.                                 |
| `publisher`    | no     | The publisher's id.                                               |
| `entry`        | no     | The plugin's entry type. The server does not read it today.       |
| `author`, `license`, `projectUrl`, `docs` | no | Shown to the owner.                            |
| `autoEnabled`  | no     | Whether the plugin starts after install. Default `true`.          |
| `capabilities` | no     | What the plugin may reach. See [the capability list](https://docs.nomercy.tv/nomercy-plugins/capabilities). |
| `dependencies` | no     | Other plugins this one needs. See [Dependencies](#dependencies).  |
| `translations` | no     | Where the plugin's text lives. See [Translations](#translations). |
| `settings`     | no     | The fields the server draws on the plugin's settings page.        |
| `ui`           | no     | Reserved. Screens are declared under `capabilities`.              |
| `signature`    | no     | The publisher signature, written by the marketplace.              |

`version` has to read as a .NET version: two to four numbers with dots,
such as `1.0` or `1.0.0`. A suffix like `-beta` is refused.

Every field added after a manifest was written is optional and has a default,
so an older manifest keeps loading.

## Dependencies

A plugin that needs another plugin names it:

```json
{
  "dependencies": [
    { "id": "01JB0000000000000000000000", "range": ">=1.2.0 <2.0.0", "tier": "free" }
  ]
}
```

`range` uses `>=`, `<=`, `>`, `<` and `=`. A number with no operator means
exactly that version. Comparators separated by a space must all match.
Alternatives separated by `||` match if one of them does. An empty range or
`*` matches every version. A range the server cannot read never matches.

The server checks each dependency before it enables the plugin:

- **A free plugin may depend only on free plugins.** A free plugin with a
  paid dependency is refused with `PLUGIN_DEPENDENCY_TIER_MISMATCH`. Remove
  the dependency, or make this plugin paid.
- **A paid dependency is never bought for the owner.** If the server does not
  own it, the plugin installs and stays off with
  `PLUGIN_DEPENDENCY_PAID_NOT_OWNED` until the owner buys it on nomercy.tv.
- **A missing dependency, or one outside the range,** is refused with
  `PLUGIN_DEPENDENCY_MISSING`. Install a version in the range from the
  marketplace.

## Translations

A plugin's text lives in one JSON file per language, not in `plugin.json`.
The manifest says where the files are:

```json
{
  "translations": { "source": "en", "locales": ["nl", "de"], "path": "lang" }
}
```

| Field     | Default | What it is                                           |
| --------- | ------- | ---------------------------------------------------- |
| `source`  | `en`    | The language the plugin was written in.              |
| `locales` | none    | The other languages it ships.                        |
| `path`    | `lang`  | The folder that holds the files, inside the plugin.  |

Each language is `<path>/<locale>.json`, such as `lang/en.json`. A viewer
whose language the plugin does not ship reads the `source` language. A folder
outside the plugin's own folder is never read.

A label in the manifest, such as a mount's `label`, is a key into these files
and not the text itself:

```json
{ "label": "template.nav.title" }
```

`nomercy-plugin verify` warns about a label with a space in it, because that
reads as a sentence in one language rather than a key. The server checks the
files at load and logs what is missing. A problem there is a warning, never a
reason to refuse the plugin.
