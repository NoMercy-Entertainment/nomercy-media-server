# Vendored from server/media/nomercy-disc-format — chapter/identity slice only

This folder is a narrow, deliberately incomplete vendor of
`server/media/nomercy-disc-format`: only the disc-identity and DVD-identity
seam, plus the abstraction types it depends on. It gives the media server
**real chapter marks and a real disc-identity hash**, with zero coupling to
that repo's menu-rendering (`.nmdf`) pipeline.

**Why vendored, not referenced:** `nomercy-disc-format` has no GitHub
remote — it's local-only, mid-development on `feat/nmdf` — so a cross-repo
`ProjectReference` builds fine on a machine with the whole monorepo checked
out but breaks every CI run, which clones only `nomercy-media-server` in
isolation. Same precedent as `Sources/Bluray/Bdmv/` (see that folder's
README) — this folder extends the same pattern into a second, adjacent
slice of the same upstream repo.

## What's here

- `Abstractions/Disc/` — `DiscIdentity`, `IDiscIdentityReader`,
  `DiscTranspileRequest`, `HdmvTitleData`, `DiscKind`. The shared shapes an
  identity reader and its caller both need.
- `Composition/DiscIdentityDispatcher.cs` — routes an identity read to
  whichever registered `IDiscIdentityReader` handles the disc's kind.
- `Providers/Dvd/Identity/DvdIdentityReader.cs` — hashes the structural
  header of every DVD IFO file into a stable id. No native dependency.
- `Infra/LibBluray/` — `BlurayIdentityReader` (in `Identity/`) plus the
  managed P/Invoke wrapper (`LibBluray.cs`, `LibBlurayResolver.cs`,
  `ILibBluray.cs`, `BlurayDiscInfo.cs`) and the full `Native/` binding set
  it compiles against, vendored together because `LibBlurayNative.cs`
  declares every libbluray entry point (including playback/overlay methods
  this slice never calls) in one P/Invoke class — splitting it would mean
  hand-maintaining a divergent subset. `native-bin/` carries the five
  native DLLs (`libbluray`, `libaacs`, `libbdplus`, `libgcrypt-20`,
  `libgpg-error6-0`) the resolver loads at runtime; the `.csproj` copies
  them into the build output the same way the source repo does.

`DiscContentCatalog.cs` (real chapter marks + audio/subtitle language
lists, built from the parsed `.mpls` playlists) lives in
`Sources/Bluray/Bdmv/`, not here — see that folder's README for why.

## What was deliberately excluded, and why

This is the metadata-only slice. Full menu rendering is a separate, later
effort — **do not** "helpfully" vendor the rest assuming it was an
oversight:

- **`Abstractions/Wire/`** (113 files) — the `.nmdf` menu-rendering wire
  schema (`Screen`/`Widget`/`Op`/`Node`/`ReelFile`/`MachineFile` records).
  Its consumer, menu rendering, is acknowledged incomplete/broken by the
  project owner; fixing that is explicitly out of scope for this task.
- **`Providers/Hdmv/**` and `Providers/Bdj/BdjStructuralTranspiler.cs`** —
  the HDMV/BD-J menu transpilers that produce `NmdfBundle`s. Neither
  produces chapter/identity data; both are purely part of the menu path.
- **Licensing/Integrity stubs** (`FakeLicenseTransport`,
  `PhoneHomeLicenseGate`, `StubPackSigner`, `NmdfWriter`) — packaging
  machinery for `.nmdf` packs. Nothing in this slice writes a pack.
- **The live-JVM BD-J capture/Xlet-analysis path** (`Capture/` minus 3
  files, all of `Live/`, all of `Xlet/`) — drives a real JVM to observe
  button behavior. Not needed for a structural identity hash.
- **`IBdjMenuCompiler`** — the live-JVM compiler interface. Same reason.

If `nomercy-disc-format` ever gets a GitHub remote or ships as a NuGet
package, delete this folder (and `Sources/Bluray/Bdmv/`) and swap the
`using NoMercy.DiscFormat.*` call sites to a real package reference
instead — no other change needed. Until then, keep both copies in sync by
hand if the upstream identity/chapter code changes.

## Test coverage note

`DiscIdentityDispatcher` has no existing test in `nomercy-disc-format` —
flagging the gap rather than writing new tests for vendored code in this
task.
