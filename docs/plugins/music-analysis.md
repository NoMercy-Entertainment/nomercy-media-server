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

Every type mentioned here lives in `NoMercy.Plugins.Abstractions` (ABI 10.2 or
later; see `PluginAbi.Current`). `IPluginMusicAnalysisWriter.RegisterStemsAsync`
joined 10.2 after the rest, with a default implementation rather than a version
bump: an implementer written before it keeps compiling, and only the host's own
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

Two members: `RunFilterGraphAsync` for a measurement pass that produces no
file, and `SplitStemsAsync` for a stem split that does.

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
  library track: `PluginAudioInput.Derived(storageKey)`.
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
else does.

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
| `"the input names neither a track nor a derived file"` | a `PluginAudioInput` was built with neither field set | always build inputs through `PluginAudioInput.Track(...)` or `PluginAudioInput.Derived(...)`, never the constructor directly |
| `"stemsplit timed out after 600 seconds"` | ffmpeg did not finish inside the host's 10-minute timeout (a stalled mount is the usual cause) | mark the track failed and retry on a later sweep; this is not something to retry immediately |
| `"stemsplit exited with {code}"` | ffmpeg ran and returned non-zero | read the `onStdErr` lines you captured for the real reason before deciding whether to retry |

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

Skipped entirely, rather than refused, when the track's duration is unknown:
the millisecond-range check. A partial base row is normal, not a defect.

### `IPluginMusicAnalysisWriter.RegisterStemAsync` and `RegisterStemsAsync`

| refusal | what it means | what to do |
|---|---|---|
| `"track {id} does not exist"` | unknown `TrackId` | drop the row |
| `"storage key {key} is not in the derived store"` | the stem was never actually written, or the key is wrong | write it through `IPluginDerivedAudio.PutAsync` (or `SplitStemsAsync`) first |
| `"full coverage stems must not specify a window"` | `Coverage.Full` was combined with a non-null `WindowStartMs` or `WindowEndMs` | leave both null for `Full` |
| `"windowed stems must specify both window_start_ms and window_end_ms"` | `MixIn` / `MixOut` was combined with a null window bound | set both, in milliseconds from the start of the track |
| `"window_start_ms must be less than window_end_ms"` | the window was empty or backwards | fix the bounds — a windowed stem always covers a positive span |
| `"stem {kind}/{coverage} for track {id} was written concurrently; retry"` | another sweep registered the same stem at the same moment, pointing at a different file | run the track again; a concurrent write of the *same* key is accepted silently, so this only appears when the two disagree |

### `IPluginMusicAnalysisWriter.MarkFailedAsync`

| refusal | what it means | what to do |
|---|---|---|
| `"track {id} does not exist"` | unknown `TrackId` | nothing to mark |

`DeleteDjAnalysisAsync` never refuses; deleting a row that is not there is a
no-op.

## The event to subscribe to

`TrackAudioAnalysisCompletedEvent` (`NoMercy.Events.Music`) is published by
the server's own base-analysis job the moment a `TrackAudioAnalysis` row lands
— `Ok` or `Failed` — so the plugin can react per track instead of waiting for
its next sweep tick:

```csharp
using NoMercy.Events.Music;

_subscription = context.EventBus.Subscribe<TrackAudioAnalysisCompletedEvent>(
    async (evt, ct) =>
    {
        if (evt.State != "Ok")
        {
            return; // nothing to build the DJ row from yet
        }

        await AnalyzeOneTrackAsync(evt.TrackId, ct);
    }
);
```

The event carries `TrackId`, `AnalyzerVersion` (the base analyzer's version
the row was computed at), `State` (the string `"Ok"` or `"Failed"` — the
events package carries no reference to the database enum it came from) and
`LibraryIds`.

`LibraryIds` is every library the track belonged to when the verdict landed,
read at publish time rather than carried from the moment the job was queued.
It is a list because a track can belong to several libraries at once, and it
is empty when the track is in none. Retention — how long a derived file for
this track is worth keeping — is a per-library decision, so pick the policy
per id rather than assuming one:

```csharp
foreach (Ulid libraryId in evt.LibraryIds)
{
    ApplyRetentionPolicyFor(libraryId, evt.TrackId);
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
