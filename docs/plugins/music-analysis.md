# Writing a DJ analysis plugin

How a plugin computes and stores the DJ analysis of a track: the beat grid's
downbeat and phrases, vocal activity, per-bar energy, candidate cue points and
chords, plus the separated stems a transition is rendered from. The server
owns the storage (`TrackDjAnalysis`, `TrackStem`, the derived-audio store) and
the base measurements (`TrackAudioAnalysis` — tempo, key, loudness); a plugin
that declares the right hooks computes the DJ-specific layer on top and writes
it back through the contracts below. None of this needs the plugin to know
where a file lives on disk, where ffmpeg is installed, or how to shell out to
it safely — the host mediates all three.

Every type mentioned here lives in `NoMercy.PluginSdk.Abstractions` (ABI 12.1
or later; see `PluginAbi.Current`). The assembly itself was renamed at 12.0;
`GetFailedDjAnalysisAsync` and `PluginTrackDjFailure` are what 12.1 added on
top of the rename. `IPluginMusicAnalysisWriter.RegisterStemsAsync` joined 10.2
after the rest, with a default implementation rather than a version bump: an
implementer written before it keeps compiling, and only the host's own
override writes the stems all-or-nothing.

## Declaring the hooks

Four hooks go in `plugin.json`, three of them elevated — the owner has to
grant each one explicitly before the matching member on `IPluginContext`
stops being `null`:

```json
{
  "capabilities": {
    "hooks": ["scheduledTask", "audioTools", "derivedAudio", "musicAnalysisWrite"]
  }
}
```

| hook | elevated | unlocks | for |
|---|---|---|---|
| `scheduledTask` | no | `IScheduledTaskPlugin` | running the analysis sweep on a cron |
| `audioTools` | yes | `IPluginContext.AudioTools` (`IPluginAudioTools`) | running the server's own ffmpeg build over a track or a derived file |
| `derivedAudio` | yes | `IPluginContext.DerivedAudio` (`IPluginDerivedAudio`) | the content-addressed store for stems and other files nothing in a library owns |
| `musicAnalysisWrite` | yes | `IPluginContext.MusicAnalysisWriter` (`IPluginMusicAnalysisWriter`) | writing the `TrackDjAnalysis` row and the stem register |

Read-only access to what analysis has already measured —
`IPluginContext.Music` (`IPluginMusicQuery`) — needs no hook and no grant; it
is always present.

Until the owner grants an elevated hook, its member on `IPluginContext` is
`null`. Check for that rather than assuming the grant landed:

```csharp
if (context.AudioTools is not { } audioTools)
{
    context.Logger.LogInformation("audioTools not granted yet; skipping this sweep tick");
    return;
}
```

## `IPluginAudioTools` — running ffmpeg

Three members: `RunFilterGraphAsync` for a measurement pass that produces no
file, `SplitStemsAsync` for the server's own stem split, and `RenderAsync`
(ABI 12.7) for a graph whose output the plugin keeps as files in its own data
folder.

### `RunFilterGraphAsync` and the complex-graph rule

`IPluginAudioTools` takes a track id as a `string` (`PluginAudioInput.Track`
and `SplitStemsAsync` both do) while everything else here — the query
contract, the analysis writer, the completion event — uses `Guid`. Call
`.ToString()` on the `Guid` you already have; there is no other conversion.

```csharp
PluginAudioRunResult result = await audioTools.RunFilterGraphAsync(
    PluginAudioInput.Track(trackId.ToString()),
    new PluginFilterGraph(
        "[0:a]stemsplit=model={stemsModel}[voc][acc];"
            + "[voc]ebur128=metadata=1[voclevel];[voclevel]anullsink;"
            + "[acc]ebur128=metadata=1[acclevel];[acclevel]anullsink",
        Complex: true
    ),
    onStdOut: null,
    onStdErr: line => context.Logger.LogTrace("{Line}", line),
    ct: ct
);

if (!result.Ran)
{
    context.Logger.LogWarning("filter graph refused: {Reason}", result.Refusal);
    return;
}
```

- The literal token `{stemsModel}` is replaced by the host with the
  stemsplit model's file name before the graph runs — a plugin never learns
  the path, and the process runs with the ffmpeg folder as its working
  directory so a bare file name resolves.
- `Complex: false` runs the text as `-af <text> -f null -`, a single input to
  a single output — fine for a one-filter measurement such as
  `"ebur128=metadata=1"`.
- `Complex: true` runs it as `-filter_complex <text> -map 0:a -f null -`. The
  host only ever maps the **untouched** input stream to the null muxer; it
  never maps anything the plugin's own graph produced. That means every
  branch the plugin's text forks off — one per stem, one per measurement —
  has to be consumed inside the plugin's own text or ffmpeg refuses to run at
  all. The pattern above shows it: `stemsplit` forks into `[voc]` and `[acc]`,
  each is measured and re-labelled, and each measured label ends in
  `anullsink`, a filter that consumes audio and produces nothing. Nothing in
  this graph is written to disk — the point of a measurement pass is what the
  filters print, not a file — and `RunFilterGraphAsync` never returns any
  audio; it returns only the exit code and whatever `onStdOut` / `onStdErr`
  read live.
- Reading a stem already written to the derived store as input, instead of a
  library track: `PluginAudioInput.Derived(storageKey)`. A file in the plugin's
  own data folder: `PluginAudioInput.PluginFile(relativePath)` (ABI 12.7).
- One ffmpeg run at a time per plugin; a second call while one is running is
  refused rather than queued. A 10-minute timeout applies on top of your own
  cancellation token.

### `SplitStemsAsync`

```csharp
PluginStemSplitResult split = await audioTools.SplitStemsAsync(
    trackId.ToString(),
    PluginStemCoverage.MixIn,
    PluginStemSet.Two,
    ct
);

if (!split.Accepted)
{
    context.Logger.LogWarning("stem split refused for {TrackId}: {Reason}", trackId, split.Refusal);
    return;
}

foreach (PluginStemFile stem in split.Stems)
{
    context.Logger.LogInformation("{Kind}: {Bytes} bytes at key {Key}", stem.Kind, stem.Bytes, stem.StorageKey);
}
```

`coverage` picks how much of the track gets split:

| value | what it covers |
|---|---|
| `PluginStemCoverage.Full` | the whole track, start to end |
| `PluginStemCoverage.MixIn` | the first 20 % — the window a mix *into* the next track draws from |
| `PluginStemCoverage.MixOut` | the last 25 % — the window a mix *out of* the track draws from |

The exact bound is computed by the host from the track's duration, never by
the caller. Both stems land in the derived store and are registered in the
stem register automatically — a successful `SplitStemsAsync` call is enough;
there is no separate `RegisterStemAsync` call to make for it (that member
exists for `IPluginMusicAnalysisWriter`'s own callers, described below).
`stemSet` is `PluginStemSet.Two` today (vocals + accompaniment);
`PluginStemSet.Four` is reserved and currently refused.

### `RenderAsync` — keeping the audio a graph produces

`RunFilterGraphAsync` throws its audio away and `SplitStemsAsync` is fixed to
one model, one codec and the server's own store. `RenderAsync` (ABI 12.7) is
the general call: one complex graph over one to eight inputs, each labelled
output pad encoded to a file in the plugin's own data folder
(`context.Storage.Private`, which is also `DataFolderPath`). It needs the same
`audioTools` hook and nothing more.

```csharp
PluginAudioRunResult result = await audioTools.RenderAsync(
    new PluginAudioRender(
        Inputs:
        [
            // The last thirty seconds of one track and the first thirty of the next.
            new PluginAudioSource(
                PluginAudioInput.Track(outgoingId.ToString()),
                Start: TimeSpan.FromSeconds(210),
                Duration: TimeSpan.FromSeconds(30)
            ),
            new PluginAudioSource(
                PluginAudioInput.Track(incomingId.ToString()),
                Duration: TimeSpan.FromSeconds(30)
            ),
        ],
        GraphText: "[0:a][1:a]acrossfade=d=8[mix]",
        Outputs: [new PluginAudioOutput("mix", "transitions/a-to-b.opus", PluginAudioCodec.Opus, BitrateKbps: 160)]
    ),
    onStdOut: null,
    onStdErr: line => context.Logger.LogTrace("{Line}", line),
    new PluginAudioRunOptions(Timeout: null),
    ct
);

if (result is { Ran: true, ExitCode: 0 })
{
    // result.Outputs: [{ RelativePath: "transitions/a-to-b.opus", Bytes: … }]
    PluginMediaUrl url = await context.Media.Proxy.MintFileAsync("transitions/a-to-b.opus", ct);
}
```

- **The host writes the command line.** The plugin supplies the graph and names
  the pads; it never writes an ffmpeg argument and never learns a library path.
  Inputs are addressed by position in the graph: `[0:a]`, `[1:a]`, and so on.
  A window (`Start`, `Duration`) trims that one input before the graph sees it,
  with an exact seek.
- **The graph is always a complex graph**, so its inputs and outputs are
  labelled. `Pad` is an output label without the brackets, and each output takes
  a pad of its own. The tokens `{stemsModel}`, `{whisperModel}`, `{vadModel}`,
  `{alignModel}` and `{pluginFile:…}` are replaced as they are for
  `RunFilterGraphAsync`.
- **Three codecs**, and the codec decides the container whatever the file is
  called: `Opus` (Ogg, for something a listener hears; `BitrateKbps` 6 to 510,
  and only at 8000, 12000, 16000, 24000 or 48000 Hz), `Flac` and `Wav` (16-bit
  PCM) for an intermediate.
- **Paths stay inside the data folder.** An output path and a
  `PluginAudioInput.PluginFile` path go through the checks a `{pluginFile:…}`
  token gets, and are refused in the same words, before ffmpeg starts. Folders
  on the way to an output are created.
- **Nothing half-written is left.** Each output is written under a temporary
  name (it starts with `.nomercy-render-`; leave those alone) and takes its
  real name only once ffmpeg has exited 0, all outputs together. A run that
  failed, timed out or was cancelled leaves no file and no folder it made, and
  a file that was already at the output path stays as it was.
- **The disk quota applies.** The files count against the plugin's disk
  allowance like anything else it writes. A run whose files outgrow it is
  ended while it is still writing, and refused.
- **One run per plugin at a time**, shared with the other two members.
- **`onStdOut` carries ffmpeg's progress report** next to whatever the graph
  itself prints: `key=value` lines (`out_time_us=…`, `total_size=…`,
  `progress=continue`) every five seconds, closed by `progress=end`. The host
  reads that closing line as the run having finished its files, so a graph
  must not print a line of its own that reads exactly `progress=end`.

The exit code says how a run that started ended:

| `ExitCode` | meaning |
|---|---|
| `0` | finished; `Outputs` lists the files and their sizes |
| above `0`, or ffmpeg's own negative code | ffmpeg failed; nothing was kept, read `onStdErr` for the reason |
| `PluginAudioRunResult.TimedOutExitCode` (`-2`) | the host ended it at its total timeout |
| `PluginAudioRunResult.StalledExitCode` (`-3`) | the host ended it because it made no progress for `StallTimeout` |
| `PluginAudioRunResult.RefusedExitCode` (`-1`) | never started, or its result was not kept; `Refusal` says why |

#### Runs longer than an hour: `StallTimeout`

`PluginAudioRunOptions.Timeout` is the total a run may take: ten minutes by
default, sixty at most. Some filters run slower than real time on a CPU (a
RoFormer stem split, whisper), so no fixed total holds every track. For those,
set `StallTimeout`:

```csharp
new PluginAudioRunOptions(Timeout: null) { StallTimeout = TimeSpan.FromMinutes(5) }
```

The run is then judged by its progress: it may take as long as it needs while
it keeps moving, and is ended with `StalledExitCode` once it has made no
progress for that long. With a stall limit, `Timeout` is no longer clamped to
sixty minutes, and `null` for it means no total limit at all.

Progress is ffmpeg's own report moving on: a later `out_time_us`, or a larger
`total_size`. ffmpeg prints that report every five seconds whether or not
anything moved, so a report that repeats the last position does not count. The
clock starts with the run, so the limit has to cover loading a model and the
longest stretch a filter works before it hands anything on. A stall limit under
ten seconds is raised to ten, twice the time between two reports, and one above
sixty minutes is clamped to sixty. `RunFilterGraphAsync` ignores `StallTimeout`:
that run prints no progress report to judge it by.

## `IPluginDerivedAudio` — the derived-audio store

A content-addressed store for files nothing in a library owns — stems,
later rendered transitions. Addressed by a key `PutAsync` returns, never by
path: `IDerivedAudioStore.RelativePath` (the thing that turns a key into an
on-disk path) is deliberately not exposed here, so a plugin always goes
through this facade to read or write.

```csharp
await using FileStream localFile = File.OpenRead(renderedSegmentPath);
string key = await context.DerivedAudio!.PutAsync(localFile, "audio/opus", ct);

// later, on a different pass:
if (await context.DerivedAudio.ExistsAsync(key, ct))
{
    await context.DerivedAudio.TouchAsync(key, ct); // keep it out of the next eviction sweep
    await using Stream? content = await context.DerivedAudio.OpenReadAsync(key, ct);
}

await context.DerivedAudio.DeleteAsync(key, ct);
```

`SplitStemsAsync` already writes into this store on your behalf; call
`PutAsync` yourself only for a file your own code produced (a render, an
intermediate you want to keep across runs). Two plugins that derive the same
bytes from the same track share one copy rather than each staging its own.

An entry is both halves — the content file and its register row — so
`OpenReadAsync` answers `null` (and `ExistsAsync` `false`) for a content file
that has no register row, which is what a crash between the store's
move-into-place and its register insert leaves behind. Nothing can address
such a file, and the server's own orphan sweep reclaims it on a later
eviction run; there is nothing for a plugin to clean up.

## `IPluginMusicAnalysisWriter` — writing the DJ record

### `UpsertDjAnalysisAsync`

```csharp
IReadOnlyList<PluginTrackAudioAnalysis> baseRows = await context.Music!.GetAnalysisAsync([trackId], ct);
PluginTrackAudioAnalysis baseAnalysis = baseRows.Single();

PluginWriteResult result = await context.MusicAnalysisWriter!.UpsertDjAnalysisAsync(
    new PluginTrackDjAnalysis(
        TrackId: trackId,
        DjAnalyzerVersion: CurrentDjAnalyzerVersion,
        BaseAnalyzerVersion: baseAnalysis.AnalyzerVersion,
        DownbeatIndex: 0,
        BeatsPerBar: 4,
        PhraseLengthBars: 8,
        PhraseStartsMs: [0, 15_000, 30_000, 45_000],
        VocalRegionsMs: [[4_200, 18_900], [22_000, 40_500]],
        BarEnergy: [-18.2, -17.9, -16.4, -15.8],
        CuePoints: [new PluginCuePoint(28_600, "drop", "mixIn", 0.92)],
        Chords: [new PluginChord(0, "Am"), new PluginChord(4_000, "F")]
    ),
    ct
);

if (!result.Ok)
{
    context.Logger.LogWarning("DJ analysis refused for {TrackId}: {Reason}", trackId, result.Refusal);
}
```

`BaseAnalyzerVersion` has to match the version on a **current, Ok** base row —
read it back from `IPluginMusicQuery.GetAnalysisAsync` rather than hard-coding
it, since the server can bump its own analyzer independently of yours. See
[Refusals](#refusals-what-they-mean-and-what-to-do) for every way this call
can be turned down.

### `RegisterStemAsync`, `RegisterStemsAsync`, `MarkFailedAsync`, `DeleteDjAnalysisAsync`

`RegisterStemAsync` is what `SplitStemsAsync` calls internally; call it
yourself only if you produced a stem file some other way (through your own
`RunFilterGraphAsync` graph, say) and already hold its derived-store key.

```csharp
await context.MusicAnalysisWriter!.RegisterStemAsync(
    new PluginTrackStem(
        trackId,
        Kind: "vocals",
        Coverage: PluginStemCoverage.MixIn,
        WindowStartMs: 0,
        WindowEndMs: 60_000,
        Format: "opus",
        SampleRate: 48_000,
        StorageKey: key,
        ProducerVersion: "spleeter-2stems-f16@9.0-NoMercy-MediaServer"
    ),
    ct
);
```

`RegisterStemsAsync(stems, ct)` registers several stems as one write: every
stem is validated before any row is staged, and all of them are saved in one
transaction, so a refusal on the second stem of a pair leaves the first one
unwritten instead of half a split in the register. Use it whenever the stems
belong together — `SplitStemsAsync` registers its pair through it. The refusal
you get back is the first one any stem earned, from the same list as
`RegisterStemAsync` below.

`MarkFailedAsync(trackId, djAnalyzerVersion, baseAnalyzerVersion, reason, ct)`
records that analysis was attempted and did not produce a row — so the sweep
can tell "not analysed yet" from "analysed and failed" instead of retrying
the same broken file forever. It clears every measurement column on the row
(a `Failed` row must never go on carrying a previous `Ok` row's numbers).
`DeleteDjAnalysisAsync(trackId, ct)` removes the row outright and never
refuses, even for a track that never had one.

## The two "needs" queries, and how to page them

Both live on `IPluginMusicQuery` (`context.Music`, always present, no hook
needed) and both are capped server-side: `take` is clamped to 1–1000
regardless of what you pass, and a `libraryId` that does not parse comes back
as an empty page rather than a throw.

`GetTracksNeedingDjAnalysisAsync(libraryId, djAnalyzerVersion, skip, take, ct)`
is the sweep's worklist: track ids in the library whose base analysis is `Ok`
but that have no DJ row, or have one from an older `DjAnalyzerVersion`, or
have one computed against a `BaseAnalyzerVersion` the base row has since moved
past, or have one left `Pending`. A `Failed` row at the current versions is
**not** returned — a version bump or an explicit retry re-queues it, nothing
else does; [the failed rows](#failed-rows-and-how-to-release-them) are a query
of their own.

`GetTracksMissingStemsAsync(libraryId, producerVersion, policy, skip, take, ct)`
is the same idea for stems: track ids whose DJ row is `Ok` but whose
`TrackStem` rows at `producerVersion` do not yet satisfy `policy` (see
[retention](#stem-retention-policy) below). Passing
`PluginStemPolicy.OnDemand` always returns an empty page without touching the
database — under that policy there is no sweep to drive.

Page either one with `skip`, advancing it by how many rows actually came
back (not by the page size you asked for — the last page is usually short):

```csharp
const int PageSize = 500;
int skip = 0;

while (true)
{
    IReadOnlyList<Guid> page = await context.Music!.GetTracksNeedingDjAnalysisAsync(
        libraryId,
        CurrentDjAnalyzerVersion,
        skip,
        PageSize,
        ct
    );

    if (page.Count == 0)
    {
        break;
    }

    foreach (Guid trackId in page)
    {
        await AnalyzeOneTrackAsync(trackId, ct);
    }

    skip += page.Count;
}
```

### Failed rows, and how to release them

`GetFailedDjAnalysisAsync(libraryId, djAnalyzerVersion, skip, take, ct)` (ABI
12.1) returns one `PluginTrackDjFailure(TrackId, BaseAnalyzerVersion, Reason,
FailedAt)` per track in the library whose DJ row is `Failed` at that version:
the rows the needs-query keeps out. `Reason` is what your plugin gave
`MarkFailedAsync`, `FailedAt` is when it did. Same clamp and same paging as
the two queries above, except that nothing you do while reading moves the
rows, so advance `skip` by the page size as usual.

Releasing a track is `DeleteDjAnalysisAsync(trackId, ct)`: with the row gone
the needs-query returns the track again on its next page, and a run that
fails again marks it failed again. If you release a whole library, page at
`skip: 0` until the page comes back empty, because every delete shortens the
result.

## Stem retention policy

`PluginStemPolicy` (an enum on the abstractions package, not a setting the
server stores — the setting itself belongs to the plugin's own
`IPluginConfiguration`, per library, and lands in a later plugin release) has
three values:

| value | at analysis time | at plan time |
|---|---|---|
| `Windows` (0, default) | run the measurement pass in-stream (nothing stored), then `SplitStemsAsync` for `MixIn` and `MixOut` | use the stored windows |
| `Full` (1) | `SplitStemsAsync` with `Full` coverage first, then run the measurement pass reading the stored stems back through `PluginAudioInput.Derived` | use the stored full stems |
| `OnDemand` (2) | run the measurement pass in-stream, store nothing | `SplitStemsAsync` for whichever window is needed, kept only for the render |

Changing the setting later is never destructive. `Windows → Full` just means
`GetTracksMissingStemsAsync` starts returning tracks again — the sweep fills
in the missing full stems on its own. `Full → Windows` leaves the existing
full-coverage rows in place until the server's own eviction sweep reclaims
them; nothing has to be deleted by hand. Either way the dashboard's
size cap (below) is the hard limit regardless of what the policy asks for.

## Versions, and what a bump means

Three independent version numbers, each owned by a different thing:

| version | owner | lives in | a bump means |
|---|---|---|---|
| base analyzer | the server | `TrackAudioAnalysis.AnalyzerVersion` | the server's own sweep redoes base rows; any `TrackDjAnalysis` row whose `BaseAnalyzerVersion` no longer matches becomes stale and `GetTracksNeedingDjAnalysisAsync` starts returning that track again |
| DJ analyzer | your plugin | `TrackDjAnalysis.DjAnalyzerVersion` (`PluginTrackDjAnalysis.DjAnalyzerVersion` on the wire) | every row from an older version is stale; the needs-query returns every one of them, so raising this constant is how you redo your whole library after changing a stage |
| stems producer | your plugin (model + ffmpeg build) | `TrackStem.ProducerVersion` (`PluginTrackStem.ProducerVersion`) | new stem rows are written at the new value; old rows at the old value are left alone until the derived-audio store evicts them — a model or ffmpeg upgrade never has to delete anything itself |

`ProducerVersion` is a free-form string you choose; `SplitStemsAsync` builds
its own as `"{model name}@{ffmpeg version token}"` (for example
`spleeter-2stems-f16@9.0-NoMercy-MediaServer`) when it writes stems through
the built-in path, so use the same shape if you register stems yourself.

## Refusals: what they mean and what to do

Every method here fails by returning a refusal in words, never by throwing —
a sweep runs unattended, and a stack trace nobody reads is worth nothing next
to a reason the owner can act on. Check `Ran` / `Accepted` / `Ok` before
touching the rest of the result.

### `IPluginAudioTools`

| refusal | what it means | what to do |
|---|---|---|
| `"ffmpeg is not installed"` | no ffmpeg path is configured on this server, or the configured path does not exist | tell the owner to point the server at its ffmpeg build; nothing a plugin can fix at runtime |
| `"the stemsplit model is not installed"` | the Spleeter GGUF is missing (checked whenever the filter text contains `"stemsplit"`, or always for `SplitStemsAsync`) | tell the owner to install the model; retry later, the capability probe re-checks periodically |
| `"another ffmpeg run of this plugin is still in progress"` | one ffmpeg process at a time per plugin; a second call arrived while the first was still running | do not retry immediately — queue the work and wait for the current run to finish |
| `"four-stem splitting is not available yet"` | `PluginStemSet.Four` was requested | use `PluginStemSet.Two`; four-stem support is a later ffmpeg release |
| `"track {id} has no file"` | the id did not parse as a `Guid`, the track is unknown, or the library scan never recorded a file for it | skip the track, or mark it failed — there is nothing to run ffmpeg against |
| `"track {id} has no duration"` | `SplitStemsAsync` was asked for `MixIn` or `MixOut` on a track the library never timed | fall back to `PluginStemCoverage.Full`, or wait for a rescan to record a duration |
| `"storage key {key} is not in the derived store"` | `PluginAudioInput.Derived(key)` named a key nothing wrote, or the key is empty | double-check the key came from a `PutAsync` or `SplitStemsAsync` call that actually succeeded |
| `"the input names neither a track nor a derived file"` | a `PluginAudioInput` was built with no field set | always build inputs through `PluginAudioInput.Track(...)`, `PluginAudioInput.Derived(...)` or `PluginAudioInput.PluginFile(...)`, never the constructor directly |
| `"the input names more than one source"` | a `PluginAudioInput` names a plugin file and a track or derived file at once | build it through one of the three factory methods |
| `"stemsplit timed out after 600 seconds"` | ffmpeg did not finish inside the host's 10-minute timeout (a stalled mount is the usual cause) | mark the track failed and retry on a later sweep; this is not something to retry immediately |
| `"stemsplit exited with {code}"` | ffmpeg ran and returned non-zero | read the `onStdErr` lines you captured for the real reason before deciding whether to retry |

`RenderAsync` adds these, every one of them before ffmpeg starts unless it says
otherwise:

| refusal | what it means | what to do |
|---|---|---|
| `"plugin file '{path}' leaves the plugin's data folder"` | an output path or a plugin-file input has a `..` segment, resolves outside the folder, or is reached through a link | name a path inside `context.Storage.Private` |
| `"plugin file '{path}' is an absolute path; only a path inside the plugin's data folder is allowed"` | the path is rooted or starts with a drive letter | pass the path relative to the data folder |
| `"plugin file '{path}' does not exist"` | a `PluginAudioInput.PluginFile` names a file that is not there | write the file first |
| `"an output path names no file"` / `"a plugin file input names no file"` | the path is empty | give the output or the input a path |
| `"a render needs at least one input"` / `"…at least one output"` | an empty list | a render reads 1 to 8 inputs and writes 1 to 8 outputs |
| `"a render reads at most 8 inputs; this one names {n}"` / `"a render writes at most 8 outputs; this one names {n}"` | more than eight | split the work over several renders |
| `"input {n} starts before the beginning of its file"` / `"input {n} has a duration that is not above zero"` | a negative `Start`, or a `Duration` of zero or less | pass `null` for no window |
| `"the graph is empty"` | `GraphText` is blank | supply the graph |
| `"output '{path}' names no pad"` / `"pad '{pad}' is written without brackets"` / `"pad '{pad}' feeds more than one output"` | the pad is missing, was written as `[pad]`, or is used twice | name each output's pad once, without brackets; use `asplit` to feed two files from one signal |
| `"output '{path}' is named more than once"` / `"output '{path}' is a folder"` | two outputs share a path (case does not tell them apart), or the path is an existing folder | give each output a file path of its own |
| `"output '{path}' sets a bitrate, which only Opus has"` / `"output '{path}' asks for {n} kbit/s; Opus takes 6 to 510"` | a bitrate on FLAC or WAV, or one outside what Opus encodes | leave `BitrateKbps` null for a lossless codec |
| `"output '{path}' asks for {n} Hz; Opus encodes at 8000, 12000, 16000, 24000, 48000 Hz only"` | a sample rate libopus cannot encode | use 48000, or leave `SampleRate` null and resample in the graph |
| `"the whisper model is not installed"` / `"the VAD model is not installed"` / `"the stemsplit model is not installed"` | the graph names a model through its token and that model is missing. Unlike `RunFilterGraphAsync`, a render only asks for the server's stemsplit model when the graph names it, so `stemsplit` on a model file of the plugin's own runs without it | tell the owner to install the model |
| `"the render wrote no file for pad '{pad}'"` | after the run: ffmpeg exited 0 but a pad produced no file | check the graph really feeds that pad |
| `"the render does not fit the plugin's disk quota. …"` | during or after the run: the files would take the plugin past its disk allowance; none was kept | free space in the plugin's folder, or have the owner raise the allowance |
| `"the rendered files could not be put in place; a file they replace may still be open"` | after the run: a file at an output path could not be replaced; nothing changed | close the file and render again |
| `"this host cannot render audio to a file"` | the host implements an ABI older than 12.7 | check `PluginAbi.Current` before relying on it |

### `IPluginMusicAnalysisWriter.UpsertDjAnalysisAsync`

Checked in this order — the first failing check is the one you get back:

| refusal | what it means | what to do |
|---|---|---|
| `"track {id} does not exist"` | the `TrackId` is not one the library knows | drop the row you were about to write |
| `"no base analysis at version {version} for track {id}"` | no `TrackAudioAnalysis` row is `Ok` at exactly `BaseAnalyzerVersion` | re-read `IPluginMusicQuery.GetAnalysisAsync` for the current `AnalyzerVersion` and recompute against that, rather than a version you cached earlier |
| `"beats_per_bar must be at least 1, got {value}"` | `BeatsPerBar` was 0 or negative | a plugin bug — this is normally 4 |
| `"downbeat_index value {value} lies outside the beat grid (0..{max})"` | `DownbeatIndex` was outside `0 .. BeatsPerBar − 1` | clamp it, or leave it `null` when the detector could not place the bar |
| `"{field} value {value} lies outside the track (0..{durationMs} ms)"` | a millisecond value in `phrase_starts_ms`, `vocal_regions_ms`, `cue_points` or `chords` (checked in that order) falls before 0 or after the track's own duration | the detector measured past the end of the file, or against the wrong track's duration — re-check the source of the timestamp |
| `"vocal_regions_ms entry {index} must be [start, end] with start < end"` | one entry of `vocal_regions_ms` was not a pair, or its start was not before its end | a region is exactly two values; drop the malformed one or fix the bounds the detector produced |
| `"phrase_starts_ms must be ascending"` | the phrase boundaries were not strictly increasing | sort them before writing; two phrases cannot share a millisecond |
| `"{field} exceeds 64 kB"` | one JSON column (`phrase_starts_ms`, `vocal_regions_ms`, `bar_energy`, `cue_points` or `chords`, checked in that order) serialized past the 64 kB column limit | this record is meant to hold bars and phrases, not a sample-accurate trace — keep arrays proportionate to track length |
| `"the DJ record for track {id} was written concurrently; retry"` | another sweep upserted the same track at the same moment and its row is the one stored | run the track again |
| `"the DJ record for track {id} could not be stored: {exception}"` | the write failed for a reason of its own and no row is there to explain it as a race | not a retry: the server logged the exception, so read its log first |

Skipped entirely, rather than refused, when the track's duration is unknown:
the millisecond-range check. A partial base row is normal, not a defect.

### `IPluginMusicAnalysisWriter.RegisterStemAsync` and `RegisterStemsAsync`

| refusal | what it means | what to do |
|---|---|---|
| `"track {id} does not exist"` | unknown `TrackId` | drop the row |
| `"storage key {key} is not in the derived store"` | the stem was never actually written, or the key is wrong | write it through `IPluginDerivedAudio.PutAsync` (or `SplitStemsAsync`) first |
| `"stem format {format} does not match the stored content type {contentType}"` | the row would claim a format the stored file is not (`opus` goes with `audio/ogg` or `audio/opus`, `flac` with `audio/flac`) | register the stem under the format the file was actually put with — a client is handed the file by that content type |
| `"the server could not complete this call: InvalidOperationException"` | the register row behind the key is there but its content type is blank — a value only the server writes into a column that does not take null, so this is the server's own register being wrong rather than anything about your stem. A row that has *gone* instead (eviction took it between two checks) is the ordinary `"storage key … is not in the derived store"` above, not this | not a retry: the server logged it. Put the file again to mint a fresh entry, and tell the owner the log line is worth reading |
| `"full coverage stems must not specify a window"` | `Coverage.Full` was combined with a non-null `WindowStartMs` or `WindowEndMs` | leave both null for `Full` |
| `"windowed stems must specify both window_start_ms and window_end_ms"` | `MixIn` / `MixOut` was combined with a null window bound | set both, in milliseconds from the start of the track |
| `"window_start_ms must be less than window_end_ms"` | the window was empty or backwards | fix the bounds — a windowed stem always covers a positive span |
| `"stem {kind}/{coverage} for track {id} was written concurrently; retry"` | another sweep registered the same stem at the same moment, pointing at a different file | run the track again; a concurrent write of the *same* key is accepted silently, so this only appears when the two disagree |
| `"stem {kind}/{coverage} for track {id} could not be stored: {exception}"` | the write failed for a reason of its own — a foreign key, a column constraint, the disk — and no row is there to explain it as a race | not a retry: the server logged the exception, so read its log before writing the stem again |
| `"stems must not be null"` | `RegisterStemsAsync` was handed a null list | pass an empty list, or the stems you meant to write |
| `"stems contains the same stem twice: {kind}/{coverage}"` | two entries of one batch address the same register row (same track, kind, coverage and producer version) | one row per (track, kind, coverage, producer version); drop the duplicate before calling |

Three more, for a member left out entirely: `"format must not be empty"`,
`"kind must not be empty"` and `"producer_version must not be empty"` — every
string member of a stem is required, and whitespace is as absent as null.

`format` and `kind` are stored lower-cased whatever casing you write them in
(`"OPUS"` and `"Vocals"` land as `opus` and `vocals`), so one stem is one row
however two passes of your own code happen to spell it; refusals quote the
casing you sent. The whole-batch checks (`stems must not be null`, the
duplicate check) run first; then, per stem, the track before anything else —
the same order `UpsertDjAnalysisAsync` uses — then the members, then the key,
the format pairing and the window.

### `IPluginMusicAnalysisWriter.MarkFailedAsync`

| refusal | what it means | what to do |
|---|---|---|
| `"track {id} does not exist"` | unknown `TrackId` | nothing to mark |

`DeleteDjAnalysisAsync` never refuses; deleting a row that is not there is a
no-op.

## How the host scopes a facade

Three shapes, one rule each — how much state a facade holds decides how long
it lives:

- **No per-plugin state at all** — one shared singleton for every plugin.
  `PluginDerivedAudio` is this: it forwards to the server's own store and
  holds nothing of its own. It has no plugin of its own to name either, so
  the warning it logs when a call fails inside the server carries the empty
  ULID as its `PluginId` — every entry the host writes about a plugin call
  uses one template, `plugin {PluginId}: {Member} failed inside the server`,
  with the plugin and the member as structured properties.
- **Stamps the plugin id but holds no state** — built per call by a factory,
  so nothing has to be cached or invalidated.
  `PluginMusicAnalysisWriterFactory` is this: every write is stamped with the
  calling plugin's id, and the writer itself is cheap to build.
- **Holds per-plugin state** — cached per plugin id by its factory, because a
  guard a caller can get a fresh copy of guards nothing.
  `PluginAudioToolsFactory` is this: the one-ffmpeg-at-a-time semaphore is a
  field on the instance.

## The event to subscribe to

`TrackAudioAnalysisCompletedEvent` (`NoMercy.Events.Music`) is published by
the server's own base-analysis job the moment a `TrackAudioAnalysis` row lands
— `Ok` or `Failed`. It stays on the host's own event bus: `IPluginContext` has
no member that reaches it, so a plugin never sees it directly and never holds
the host bus itself. What a plugin subscribes to instead is
`PluginTopics.MusicAnalysisCompleted`, republished on the topic facade a
plugin already uses for its own events, `IPluginContext.Events.Subscribe`,
with the payload `PluginMusicAnalysisCompleted`:

```csharp
context.Events.Subscribe<PluginMusicAnalysisCompleted>(
    PluginTopics.MusicAnalysisCompleted,
    async (completed, ct) =>
    {
        if (completed.State != "Ok")
        {
            return; // nothing to build the DJ row from yet
        }

        await AnalyzeOneTrackAsync(completed.TrackId, ct);
    }
);
```

`PluginMusicAnalysisCompleted` carries the same four values
`TrackAudioAnalysisCompletedEvent` does — `TrackId`, `AnalyzerVersion` (the
base analyzer's version the row was computed at), `State` (the string `"Ok"`
or `"Failed"` — the payload carries no reference to the database enum it came
from) and `LibraryIds`, as the Ulid text form, since a topic payload crosses a
plugin's own load context and carries no host type.

`LibraryIds` is every library the track belonged to when the verdict landed,
read at publish time rather than carried from the moment the job was queued.
It is a list because a track can belong to several libraries at once, and it
is empty when the track is in none. Retention — how long a derived file for
this track is worth keeping — is a per-library decision, so pick the policy
per id rather than assuming one:

```csharp
foreach (string libraryId in completed.LibraryIds)
{
    ApplyRetentionPolicyFor(Ulid.Parse(libraryId), completed.TrackId);
}
```

## The dashboard cap setting

The derived-audio store (stems, and later rendered transitions) is capped by
a dashboard setting, `derived_audio_cap_gb` (50 GB by default, at least 1).
An hourly job evicts the least-recently-used entries — by `LastUsedAt`, never
one touched inside the last 24 hours — until the store is back under the cap.
Eviction cascades: the file goes, its `DerivedAudio` register row goes, and
every `TrackStem` row pointing at it goes with it. A plugin does not manage
this itself; `IPluginDerivedAudio.TouchAsync` is the one lever available to
keep a specific file out of the next sweep, and a missing stem at plan time
(R2, not this slice) simply falls back to a plain crossfade rather than
failing the transition — never truncate a track, never fail closed.
