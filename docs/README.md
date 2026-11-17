# Docs index

- [`backups.md`](backups.md) — what a backup holds, when backups are made,
  and how a restore works and is undone.
- [`distributed-encoding.md`](distributed-encoding.md) — running encode jobs
  across a coordinator and remote workers.
- [`encoder-errors.md`](encoder-errors.md) — the catalog of every
  `EncoderRuleId` / `EncoderRuntimeErrorId` constant the encoder pipeline can
  emit.
- [`plugins/manifest.md`](plugins/manifest.md) — the fields of a plugin's
  `plugin.json`, its dependencies and its translations.
- [`plugins/migration.md`](plugins/migration.md) — moving a plugin built
  against an SDK older than 12 to SDK 12.
- [`plugins/music-analysis.md`](plugins/music-analysis.md) — how a plugin
  computes and stores the DJ analysis of a track.
- [`plugins/versioning.md`](plugins/versioning.md) — the plugin ABI, what a
  major and a minor mean, and which `targetAbi` to set.

Refusals link these pages through `PluginDocs`
(`src/NoMercy.PluginSdk.Abstractions/PluginDocs.cs`). Most plugin docs live on
the docs site, https://docs.nomercy.tv/nomercy-plugins/overview.
- [`refactor/wave2-runbook.md`](refactor/wave2-runbook.md) — the wave-2
  autonomous refactor execution runbook.
