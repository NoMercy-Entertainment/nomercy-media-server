# Vendored from server/media/nomercy-disc-format

`Mpls.cs`, `MplsParser.cs`, `CodingType.cs`, `CodingTypeInfo.cs`, `CodingKind.cs`,
`BigEndianReader.cs`, `DiscContentCatalog.cs` are copied
verbatim (same namespace, `NoMercy.DiscFormat.Disc.Bdmv`) from
`server/media/nomercy-disc-format/src/Infra/Disc/Bdmv/` rather than referenced
via `ProjectReference`.

`DiscContentCatalog.cs` was added 2026-08-20 alongside the
`Sources/DiscFormat/` vendoring (see that folder's README) — it builds on
`MplsPlaylist`/`MplsMark` from this same folder, so it lives here rather than
duplicating a second copy of the Bdmv types under a different namespace.
`DiscContentCatalog.Build(IReadOnlyDictionary<int, MplsPlaylist>)` turns the
parsed playlists into the disc's real chapter marks and per-title
audio/subtitle language lists — no menu rendering involved.

**Why:** that repo has no GitHub remote — it's local-only, mid-development
on `feat/nmdf` — so a cross-repo `ProjectReference` to it builds fine on a
machine with the whole monorepo checked out, but breaks every CI run,
which clones only `nomercy-media-server` in isolation. Confirmed broken in
practice: CI failed with `The type or namespace name 'DiscFormat' does not
exist` the first time this dependency shipped.

These seven files are fully self-contained (no further `NoMercy.*`
dependencies beyond each other), so vendoring them is a clean copy, not a
partial one.

**If nomercy-disc-format ever gets a GitHub remote or ships as a NuGet
package**, delete this folder and its callers' `using
NoMercy.DiscFormat.Disc.Bdmv;` keep working unchanged — swap this
`ProjectReference`/vendored-copy split for a real package reference
instead. Until then, keep both copies in sync by hand if the parser
changes upstream.
