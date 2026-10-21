# Writing a lyrics plugin

How a plugin gives music tracks time-synced lyrics: it reads what the server
already has for a track, picks or combines the lyric texts the server can
find, aligns that text to the vocal stem with the server's own ffmpeg build,
and writes line and word timings back under rules the server enforces. The
server owns the storage (the `Lyrics` JSON column on `Tracks`, plus
`LyricsSource`, `LyricsTiming` and `LyricsVersion`) and keeps serving lyrics
to every client the way it did before; a plugin that declares the right hooks
only adds timing where there is none.

Every type mentioned here lives in `NoMercy.PluginSdk.Abstractions` at ABI
12.6 or later (see `PluginAbi.Current`). Everything 12.6 added is a default
interface member or a new type, so a plugin built against 12.0 to 12.5 keeps
loading; a plugin that uses any of it declares `"targetAbi": "12.6"` and an
older server refuses it with the standard ABI refusal.

## Declaring the hooks

```json
{
  "capabilities": {
    "hooks": ["scheduledTask", "audioTools", "lyricsWrite"]
  }
}
```

| hook | elevated | unlocks | for |
|---|---|---|---|
| `scheduledTask` | no | `IScheduledTaskPlugin` | running an alignment sweep on a cron |
| `audioTools` | yes | `IPluginContext.AudioTools` (`IPluginAudioTools`) | running whisper over a stem with the server's own ffmpeg |
| `lyricsWrite` | yes | `IPluginContext.Lyrics` (`IPluginLyrics`) | reading the lyrics state and writing generated timing |

`IPluginContext.Lyrics` is null until the owner has granted `lyricsWrite`, so
a plugin checks for the facade rather than catching.

## The contract the server keeps with its clients

Every lyric time the server sends to a client is final: the owner's offset is
applied once, in every payload, and `lyrics_offset` is reported only for the
adjustment UI. `lyrics_source` says where the text came from and drives the
attribution a client shows; `lyrics_timing` says where the timing came from.
The two are separate on purpose: timing a plugin generated is usually laid
over text a provider supplied, and that text still needs its own credit.

| field | values |
|---|---|
| source | `lrclib`, `musixmatch`, `embedded`, `transcribed`, or null on a row from before provenance was recorded |
| timing | `provider`, `generated`, `none`, or null on a row from before provenance was recorded |

## Reading the state

```csharp
PluginLyricsState? state = await context.Lyrics.GetStateAsync(trackId, ct);
```

| member | meaning |
|---|---|
| `Lines` | null when the track was never checked, empty when it was checked and nothing was found; otherwise the stored lines with their stored times |
| `Source`, `Timing` | the provenance above |
| `HasSyncedTiming` | any line has a time above zero; covers a legacy row whose `Timing` is null |
| `OffsetMs` | the owner's offset, reported, not applied |
| `Version` | bumped by every lyrics write: a provider fetch, the offset, a plugin |

A plugin reads the version before it starts aligning and names it in the
write. If anything wrote in between, the write is refused and the plugin
reads again.

## Hearing which tracks need timing

The server publishes `PluginTopics.LyricsRequested`
(`"music.lyrics.requested"`, payload `PluginLyricsRequested(TrackId,
HasSyncedTiming)`) when a client asked for a track's lyrics and the served
result was missing or carried no timing. At most once per track per day, so
a plugin gets one cue rather than one per play.

```csharp
context.Events.Subscribe<PluginLyricsRequested>(
    PluginTopics.LyricsRequested,
    (requested, ct) => queue.EnqueueAsync(requested.TrackId, ct)
);
```

## Getting the text

```csharp
PluginLyricsText text = await context.Lyrics.GetTextCandidatesAsync(trackId, ct);
```

Every validated candidate the providers return, not only the winner the
server would store, plus the lyrics embedded in the file's own tags (ID3
USLT and SYLT, Vorbis `LYRICS` and `UNSYNCEDLYRICS`, MP4 lyrics). Each
`PluginTextCandidate` carries `Source`, the text `Lines` without timing,
`Language` when the source says so, and `WasSynced`. Provider calls run
inside the server's rate limits and its two-minute backoff after a provider
hiccup, and the answer is kept per track for a day, so a plugin may ask as
often as it likes. The plugin never sees a file path.

## Running whisper

```csharp
PluginFilterGraph graph = new(
    "whisper=model={whisperModel}:vad_model={vadModel}:destination=-:format=json",
    Complex: false
);
PluginAudioRunResult run = await context.AudioTools.RunFilterGraphAsync(
    PluginAudioInput.Derived(vocalsStemKey),
    graph,
    onStdOut: line => output.Append(line),
    onStdErr: null,
    new PluginAudioRunOptions(TimeSpan.FromMinutes(30)),
    ct
);
```

The tokens in `PluginFilterGraphTokens` are replaced by the server before
the graph runs:

| token | becomes |
|---|---|
| `{stemsModel}` | the stemsplit model's file name |
| `{whisperModel}` | the whisper model's file name |
| `{vadModel}` | the silero VAD model's file name |
| `{pluginFile:<relative path>}` | an absolute path inside the plugin's own data folder, escaped for both filtergraph levels, so a Windows drive colon is written as `\\:` |

The model tokens are bare file names because ffmpeg runs with the ffmpeg
folder as its working directory. A run whose graph names the whisper or VAD
model while that file is not installed is refused up front, the way a
stemsplit graph already is. A plugin file that is absolute, contains `..`, or
leaves the plugin's folder is refused. Use `destination=-` for the whisper
filter's stdout, not `pipe:1`, whose colon would need escaping.

`PluginAudioRunOptions.Timeout` is how long the run may take: null is the
server's ten minutes, anything above sixty is clamped to sixty. A five-minute
track through whisper large-v3 on a CPU takes about twelve.

## Writing the timing

```csharp
PluginWriteResult result = await context.Lyrics.WriteAsync(
    trackId,
    new PluginLyricsWrite(lines, Source: "lrclib", ExpectedVersion: state.Version),
    ct
);
if (!result.Ok)
    logger.LogInformation("not written: {Reason}", result.Refusal);
```

Each `PluginLyricLine` has the text, its `Start` in seconds, and optional
`Words`, each with `Start` and `End`. The server enforces every rule; a
refusal comes back as a `PluginWriteResult` whose reason starts with a
`PluginLyricsRefusal` code and a colon, never as an exception.

| code | when |
|---|---|
| `version_conflict` | the version moved since the plugin read it, checked again at the write itself |
| `provider_synced` | the track has synced provider lyrics: `Timing == provider`, or a legacy row with times and no provenance. **A plugin never overwrites these.** |
| `invalid_lines` | no lines; lines out of order; a start before 0 or after the track ends; a line whose first word does not start with it; word times that go backwards |
| `invalid_source` | the source is not `lrclib`, `musixmatch`, `embedded` or `transcribed` |
| `unknown_track` | the server does not know the track |

On success the lines land with `timing = generated` and the source the
plugin gave, the owner's offset is cleared, because it was tuned for the
previous timing, and the version is bumped. Word timings are stored in the
same JSON as the lines, under `words`, and are absent for clients that do
not know them.

```csharp
await context.Lyrics.ClearAsync(trackId, state.Version, ct);
```

Clearing is only for generated lyrics (`not_generated` otherwise). It resets
the lyrics and their provenance to "never checked", so the server's own
resolver fetches provider lyrics again on the next request, which is the
state from before the plugin.
