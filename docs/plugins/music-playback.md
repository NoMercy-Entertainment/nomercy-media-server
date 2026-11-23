# Following and shaping music playback

How a plugin learns what a listener plays. A scrobbler, a plugin that shows
something in time with the music, a station that follows what was played, a
plugin that prepares something for the track that comes next: each needs to
know that a track started, for whom, and what is queued behind it.

Every type mentioned here lives in `NoMercy.PluginSdk.Abstractions` at ABI
12.14 or later (see `PluginAbi.Current`). Everything 12.14 added is a new
constant or a new type, so a plugin built against an older 12 keeps loading;
a plugin that uses any of it declares `"targetAbi": "12.14"` and an older
server refuses it with the standard ABI refusal.

The server tells an in-process plugin. A plugin that runs in its own process
does not receive host topics yet.

## Declaring the capability

What a person listens to is personal. The topics below reach only a plugin
that declares `player.state` and whose owner approved it.

```json
{
  "capabilities": {
    "hooks": ["player.state"]
  }
}
```

A plugin that subscribes without it is not refused: it simply hears nothing,
and starts to hear from the moment the owner approves, without subscribing
again.

## The topics

Subscribe on `IPluginContext.Events`, with the payload type of the topic:

```csharp
context.Events.Subscribe<PluginMusicPlaybackStarted>(
    PluginTopics.MusicPlaybackStarted,
    (started, ct) => OnStartedAsync(started, ct)
);
```

| constant on `PluginTopics` | topic | when | payload |
|---|---|---|---|
| `MusicPlaybackStarted` | `music.playback.started` | a track starts for a listener, by any route: a start, next, previous, the end of the track before it, a join a player completed, the same track again under repeat one | `PluginMusicPlaybackStarted` |
| `MusicPlaybackPaused` | `music.playback.paused` | the track was paused | `PluginMusicPlaybackPosition` |
| `MusicPlaybackResumed` | `music.playback.resumed` | the track plays again | `PluginMusicPlaybackPosition` |
| `MusicPlaybackSeeked` | `music.playback.seeked` | the position jumped by two seconds or more while the track stayed the same | `PluginMusicPlaybackPosition` |
| `MusicPlaybackStopped` | `music.playback.stopped` | nothing plays for the listener any more | `PluginMusicPlaybackStopped` |
| `MusicPlaybackQueueChanged` | `music.playback.queue.changed` | the upcoming tracks changed while a track plays, whoever changed them | `PluginMusicPlaybackQueueChanged` |
| `MusicPlaybackDeviceChanged` | `music.playback.device.changed` | another device makes the sound | `PluginMusicPlaybackDeviceChanged` |
| `MusicPlaybackModeChanged` | `music.playback.mode.changed` | the listener changed repeat or shuffle while a track plays | `PluginMusicPlaybackModeChanged` |

The payloads, as they travel (camelCase JSON):

| payload | fields |
|---|---|
| `PluginMusicPlaybackStarted` | `userId`, `trackId`, `deviceId`, `positionMs`, `queue` (the ids of the upcoming tracks, the next one first), `repeat` (`off`, `one`, `all`), `shuffle`, `currentList` (the list played from, as the app's route to it, for example `/music/album/{id}`) |
| `PluginMusicPlaybackPosition` | `userId`, `trackId`, `positionMs` |
| `PluginMusicPlaybackStopped` | `userId` |
| `PluginMusicPlaybackQueueChanged` | `userId`, `trackId`, `queue` |
| `PluginMusicPlaybackDeviceChanged` | `userId`, `deviceId` (null when no device holds the session) |
| `PluginMusicPlaybackModeChanged` | `userId`, `trackId`, `repeat`, `shuffle` |

## What to rely on

- **No ticks.** The position is not sent while a track plays on. With
  started, paused, resumed and seeked a plugin works it out: the position in
  the last of those, plus the time since, while the track is not paused.
- **A seek is a jump of two seconds or more.** A player corrects the server's
  clock a little with every position report, and those steps are not
  announced. A correction of two seconds or more is, as a seek, because a
  plugin that works the position out would otherwise be off by that much.
- **A start carries its queue.** The step from one track to the next is one
  `started`, not a `started` and a `queue.changed`.
- **`positionMs` of a start is not always 0.** A join a player completed can
  enter a track some way in.
- **One announcement per change, in order.** Several changes that reach the
  listener's players in one update (a queue put in a new order in one call)
  are one announcement.
- **A handler does not hold up the music.** Announcements are published from
  a queue of their own. A handler that takes long delays the announcements
  after it, for every plugin, so hand long work to a task of your own.
- **Topics are the server's word.** A plugin cannot publish them; a publish
  is refused.
