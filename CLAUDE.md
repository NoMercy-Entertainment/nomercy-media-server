# NoMercy Media Server

The flagship media server for encoding, organizing, and streaming personal media.

> **Prime directive: leave the code more maintainable than you found it.**
> Every change is judged on whether the next person — or model — can read it,
> test it, and safely extend it. Working-but-unmaintainable is a failed change.

This file is the short, always-read contract: keep it lean and accurate. For
depth (build/run commands, architecture, SQLite restrictions, security, dev
container, provider integrations) see **`.claude/CLAUDE.md`**.

## The loop — every commit, no exceptions

1. **One slice at a time.** Make the smallest coherent change.
2. **Build clean: 0 errors AND 0 warnings.** A warning is a failure here.
3. **`dotnet test` is green.** The suite is fast — use it as your safety net, not an afterthought.
4. **`dotnet csharpier format`** every changed `.cs` file.
5. **Commit only when 2–4 all pass.** A commit that isn't build- and test-green never gets made.

If a change is too big to stay green in one slice, split it.

## Non-negotiable rules (the ones that bite most)

- **Explicit types — never `var`.** Name the type. The *only* exception is an
  unnameable anonymous-type projection (`.Select(x => new { ... })`), where C#
  requires `var`. Nowhere else.
- **License header** (the SPDX block) at the top of every new file — copy it from any existing `.cs`.
- **One public type per file**, filename matches the type.
- **Depend on abstractions, not statics.** Collaborators are injected via the
  constructor (interface + DI) — not reached through static singletons or `new`d
  inline. This is the direction the whole codebase is moving; don't grow the static pile.
- **No business logic in EF models.** Models hold data; behavior lives in services.
- **No EF Core types in interface contracts.** Repositories take/return domain
  types and `Task<List<T>>` — never `IQueryable`, `IIncludableQueryable`, or
  `MediaContext`. A repository owns its context via `IDbContextFactory<MediaContext>`.
- **`.AsNoTracking()` on every read-only EF query.**
- **Filesystem I/O goes through the storage facade.** Inject `IStorage`
  (path-validated; library/encoder/media paths) or `IStorageBackend` (raw FS, for
  dashboard/picker/drive code that browses outside library roots). Never call
  `System.IO.File.*` or `System.IO.Directory.*` directly in new code.
  (`DriveInfo.GetDrives()` and `Environment.GetFolderPath()` are fine.)
- **Packages** go in `Directory.Packages.props`, never individual `.csproj`.
- **Schema changes** require EF Core migrations — never hand-edit the schema.
- **New API endpoints** need test coverage under `tests/`.

## Naming

- Projects/Classes/Methods/Properties: PascalCase. Locals/params: camelCase.
- Private fields `_camelCase`; private constants PascalCase.
- **The `I` prefix is for interfaces only** (`IMovieRepository`, `IUserCache`) —
  never on concrete classes or value objects. Value objects live in
  `NoMercy.Database/ValueObjects/` (e.g. `VideoProfile`, `ColorPalette`).

## Modern C# (required)

- Target-typed `new()` when the type is on the left: `List<Library> libraries = new();`
- Collection expressions for empties: `List<Movie> movies = [];`
- File-scoped namespaces, always.
- Primary constructors for DI.
- Pattern matching: `is not null`, `is { Prop: not null }`.
- Async all the way: return `Task`/`Task<T>`, suffix `Async`, `ToListAsync()` for EF.
- `[JsonProperty("...")]` on serialized properties — match the casing already used in the file.

## Structure (authoritative list: `ls src/`)

- **Hosts / entry:** `NoMercy.Service` (web host), `NoMercy.Cli`, `NoMercy.App`, `NoMercy.Launcher`
- **Web:** `NoMercy.Api` (controllers, DTOs, hubs, middleware), `NoMercy.Networking`
- **Data:** `NoMercy.Database` (EF models, contexts, migrations, `ValueObjects/`),
  `NoMercy.Data` (repositories + services), `NoMercy.Storage` (filesystem facade)
- **Domain:** `NoMercy.MediaProcessing`, `NoMercy.Encoder`,
  `NoMercy.OpticalMedia`, `NoMercy.Providers` (TMDB, etc.)
- **Cross-cutting:** `NoMercy.NmSystem` (system ops), `NoMercy.Authorization`
  (user cache + policy), `NoMercy.Monitoring`, `NoMercy.Events`, `NoMercy.Setup`, `NoMercy.Resources`
- **Queue:** `NoMercyQueue`, `NoMercyQueue.Core`, `NoMercyQueue.Sqlite`, `NoMercy.Queue.MediaServer`
- **Plugins:** `NoMercy.Plugins`, `NoMercy.Plugins.Abstractions`
- **Tests:** `tests/NoMercy.Tests.*` (xUnit + FluentAssertions + Moq)

Tech stack: C# / .NET 10, ASP.NET Core, EF Core (SQLite), SignalR; solution
`NoMercy.Server.sln`; central package management in `Directory.Packages.props`.

## Release assets (don't rename casually)

GitHub release assets use fixed names (`nomercy-windows-x64.exe`,
`nomercy-linux-x64`, `nomercy_VERSION_amd64.deb`, …). Renaming them breaks
`infra/nomercy-packages` and `saas/nomercy-tv` download URLs — update those too.
