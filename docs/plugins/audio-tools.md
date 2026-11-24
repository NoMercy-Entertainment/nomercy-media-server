# Running ffmpeg for a plugin: audio tools

How a plugin measures and renders audio with the server's own ffmpeg build:
a filter graph over a library track or over a file of the plugin's own, and a
render that keeps what a graph produces as files in the plugin's own folder.
None of this needs the plugin to know where a file lives on disk, where ffmpeg
is installed, or how to shell out to it safely: the host mediates all three.

Every type mentioned here lives in `NoMercy.PluginSdk.Abstractions` (see
`PluginAbi.Current`).

## What changed in ABI 13.0

Until ABI 12 the server measured every music track itself (tempo, key,
loudness) and kept a second analysis record per track, a register of stems
and a store of derived audio that plugins wrote into. Nothing in the server
and no client read any of it, so 13.0 took it out. A plugin that wants
analysis measures it and keeps it:

| gone in 13.0 | what a plugin does now |
|---|---|
| `IPluginMusicQuery.GetAnalysisAsync`, `GetDjAnalysisAsync`, `GetStemsAsync`, `GetTracksNeedingDjAnalysisAsync`, `GetTracksMissingStemsAsync`, `GetFailedDjAnalysisAsync` | measure with `RunFilterGraphAsync` below and keep the result in the plugin's own database (`IPluginStorage.OpenDatabaseAsync`) |
| `IPluginMusicAnalysisWriter` and the `musicAnalysisWrite` hook | the same: the plugin's own database |
| `IPluginDerivedAudio`, the `derivedAudio` hook and `PluginAudioInput.Derived` | `RenderAsync` writes into the plugin's own folder, and `PluginAudioInput.PluginFile` reads such a file again |
| `IPluginAudioTools.SplitStemsAsync` | a `stemsplit` filter in a graph of the plugin's own, with `{stemsSource}` or `{stemsModel}`, rendered with `RenderAsync` |
| the topic `music.analysis.completed` | nothing replaces it: the server no longer analyses. For what a listener plays, see [music-playback.md](music-playback.md) |
| the records `PluginTrackAudioAnalysis`, `PluginTrackDjAnalysis`, `PluginTrackDjFailure`, `PluginTrackStem`, `PluginStemFile`, `PluginStemSplitResult`, `PluginMusicAnalysisCompleted`, `PluginCuePoint`, `PluginChord`, `PluginStemCoverage`, `PluginStemSet`, `PluginStemPolicy` | types of the plugin's own |

A plugin built against 12 that calls none of these keeps loading on a 13
server. A manifest that still names `derivedAudio` or `musicAnalysisWrite`
is accepted; the names mean nothing any more.

What the update does to a server, once, on its first start:

- the tables `TrackAudioAnalysis`, `TrackDjAnalysis`, `TrackStems` and
  `DerivedAudio` and the column `Libraries.AnalyzeAudio` are dropped. The
  server makes its usual backup before it migrates;
- analysis jobs that were still waiting or had failed are taken out of the
  queue, with the two schedules that fed them;
- the folder `cache/derived` is deleted and the log says how many MB that
  gave back. A plugin's own folder is not touched;
- the saved `derivedAudioCapGb` setting is removed.

A server rolled back to an older version gets the tables back empty and
measures its music again: slow, not broken.

## Declaring the hook

```json
{
  "capabilities": {
    "hooks": ["scheduledTask", "audioTools"]
  }
}
```

`audioTools` is elevated: the owner has to grant it before
`IPluginContext.AudioTools` stops being `null`. An arbitrary filter graph is
an arbitrary ffmpeg run against a library file.

## `IPluginAudioTools` — running ffmpeg

Two members: `RunFilterGraphAsync` for a measurement pass that produces no
file, and `RenderAsync` (ABI 12.7) for a graph whose output the plugin keeps
as files in its own data folder.

### `RunFilterGraphAsync` and the complex-graph rule

`PluginAudioInput.Track` takes a track id as a `string`, while the query
contract (`IPluginMusicQuery`) and the playback topics use `Guid`. Call
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
  Spleeter model's file name before the graph runs — a plugin never learns
  the path, and the process runs with the ffmpeg folder as its working
  directory so a bare file name resolves. `stemsplit={stemsSource}` (ABI
  12.10) lets the host choose the engine instead: RoFormer where this
  machine's GPU runs it, Spleeter elsewhere, with the fall back handled by
  the host; see "Stems with RoFormer" in [lyrics.md](lyrics.md).
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
- A file in the plugin's own data folder as input, instead of a library
  track: `PluginAudioInput.PluginFile(relativePath)` (ABI 12.7). That is how
  a stem a render wrote earlier is read again.
- One ffmpeg run at a time per plugin; a second call while one is running is
  refused rather than queued. A 10-minute timeout applies on top of your own
  cancellation token, except while a RoFormer stem split is running; see
  "A RoFormer stem split" below.

### `RenderAsync` — keeping the audio a graph produces

`RunFilterGraphAsync` throws its audio away. `RenderAsync` (ABI 12.7) keeps
it: one complex graph over one to eight inputs, each labelled
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
- **One run per plugin at a time**, shared with `RunFilterGraphAsync`.
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

#### A RoFormer stem split: judged by the filter's chunks

A run whose graph splits with RoFormer (a source token that became a RoFormer
preset, or a `preset=`, `models=`, `vocals_models=` or `accompaniment_models=`
of the plugin's own) is timed differently, in both calls. No fixed total
holds it: a four-minute song takes about four minutes on an RTX 3070 and about
25 on a GTX 1060, after up to a minute of loading models. ffmpeg's position
does not help either, because RoFormer hands its audio on a whole chunk at a
time. The filter prints a line for every chunk it finishes, and the host
follows those:

- Until the filter has handed out its last chunk, the run is ended only when
  no chunk line came for **five minutes**, counted from the start so a load
  that never finishes counts too. `Timeout` and `StallTimeout` do not apply
  in this phase, so the run may outlast them.
- After the last chunk, the rest of the run gets the limits it would have had
  without RoFormer, counted from that moment: your `Timeout` (or the ten
  minutes), and for a render your `StallTimeout` too.
- A run ended for want of chunk lines comes back as `StalledExitCode` (`-3`).

The chunk lines are at ffmpeg's default log level, `info`; a graph run with a
quieter `-loglevel` cannot be followed this way, so do not lower it.

One RoFormer run holds the GPU at a time, across every plugin: the four models of `preset=full` take 5.4 GB on an RTX 3070, so two runs
do not fit on most cards. A second run waits for the first to finish before
its ffmpeg starts, and the wait does not count against its limits. Spleeter
runs never wait.

## Refusals: what they mean and what to do

Every method here fails by returning a refusal in words, never by throwing —
a sweep runs unattended, and a stack trace nobody reads is worth nothing next
to a reason the owner can act on. Check `Ran` before touching the rest of the
result.

### `IPluginAudioTools`

| refusal | what it means | what to do |
|---|---|---|
| `"ffmpeg is not installed"` | no ffmpeg path is configured on this server, or the configured path does not exist | tell the owner to point the server at its ffmpeg build; nothing a plugin can fix at runtime |
| `"the stemsplit model is not installed"` | the Spleeter GGUF is missing (checked whenever the filter text contains `"stemsplit"`) | tell the owner to install the model; retry later, the capability probe re-checks periodically |
| `"another ffmpeg run of this plugin is still in progress"` | one ffmpeg process at a time per plugin; a second call arrived while the first was still running | do not retry immediately — queue the work and wait for the current run to finish |
| `"track {id} has no file"` | the id did not parse as a `Guid`, the track is unknown, or the library scan never recorded a file for it | skip the track, or mark it failed — there is nothing to run ffmpeg against |
| `"the input names neither a track nor a file of the plugin's own"` | a `PluginAudioInput` was built with no field set | always build inputs through `PluginAudioInput.Track(...)` or `PluginAudioInput.PluginFile(...)`, never the constructor directly |
| `"the input names more than one source"` | a `PluginAudioInput` names a plugin file and a track at once | build it through one of the two factory methods |

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
