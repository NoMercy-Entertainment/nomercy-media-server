# Following and shaping music playback

How a plugin learns what a listener plays, puts the listener's upcoming
tracks in another order, and supplies the audio that joins two tracks. A scrobbler, a plugin that shows
something in time with the music, a station that follows what was played, a
plugin that prepares something for the track that comes next: each needs to
know that a track started, for whom, and what is queued behind it.

Every type mentioned here lives in `NoMercy.PluginSdk.Abstractions` at ABI
12.14 or later (see `PluginAbi.Current`). Everything 12.14 added is a new
constant or a new type, so a plugin built against an older 12 keeps loading;
a plugin that uses any of it declares `"targetAbi": "12.14"` and an older
server refuses it with the standard ABI refusal.

All of it is for an in-process plugin. A plugin that runs in its own process
does not receive host topics yet and has no `IPluginContext.Player`.

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

## Reading and arranging the upcoming tracks

The server keeps every listener's music itself: the track, the position and
the upcoming tracks. `IPluginContext.Player.Music` (`IPluginMusicPlayer`)
reads that for a named listener and puts the upcoming tracks in another
order, also for a listener who never opened the plugin's page.

```json
{
  "capabilities": {
    "hooks": ["player.state", "player.queue"]
  }
}
```

| member | needs | does |
|---|---|---|
| `GetPlaybackAsync(userId)` | `player.state` | what plays now as `PluginMusicPlayback` (track, device, playing or paused, position, queue, repeat, shuffle, list), or null when nothing plays |
| `GetQueueAsync(userId)` | `player.queue` | the ids of the upcoming tracks, the next one first |
| `MoveInQueueAsync(userId, trackId, position)` | `player.queue` | moves one queued track to another place; 0 is the front, a place past the end is the end |
| `ReorderQueueAsync(userId, order)` | `player.queue` | puts all the upcoming tracks in a new order in one step |

A call without the capability throws `PluginRefusedException` with the usual
refusal. What the player's state allows is answered as a
`PluginMusicQueueAnswer`:

| answer | meaning |
|---|---|
| `Accepted` | the upcoming tracks are in the order asked for |
| `NothingPlaying` | nothing plays for this listener |
| `NotInQueue` | the track is not among the upcoming tracks, or the order names other tracks than the ones queued; nothing changed |
| `TooLate` | the change would make another track the next one, and the players were already told what comes next (ten seconds before the end of the playing track); nothing changed |

What to rely on:

- **A plugin arranges, it does not choose the tracks.** It cannot add a track
  the listener did not queue and cannot remove one. `ReorderQueueAsync` takes
  exactly the tracks that are queued, each as often as it is queued.
- **Move by track, not by index.** The queue shifts when a track ends, so a
  place read a moment ago can be another track now. A track that is queued
  more than once moves by its first place.
- **One step, one update.** After a change the listener's players are sent
  the new state once, so the queue shows in the new order everywhere, and
  `music.playback.queue.changed` is said once with the whole new order. That
  topic also reaches the plugin that made the change.
- **What plays next is what is first.** The track at the front is what the
  server plays after the playing one and what it tells the players to load.
- **`IPluginPlayer.Queue` is not this.** That member arranges sources a
  plugin plays itself on the caller's own player, and no host carries it.

## Supplying the audio that joins two tracks

The join between two tracks is a crossfade the player makes. A plugin that
has audio for that join hands it to the server, and the server passes it to
the listener's players in time: a transition rendered from the two tracks, a
station ident, a spoken introduction.

```json
{
  "capabilities": {
    "hooks": ["player.state", "player.queue", "media.proxy"]
  }
}
```

An offer needs `player.queue`, and `media.proxy` because the server serves
the file. `player.state` is for the topics that say when to offer.

```csharp
PluginMusicTransitionAnswer answer = await context.Player.Music.OfferTransitionAsync(
    userId,
    new PluginMusicTransition(
        FromTrackId: playing,
        ToTrackId: next,
        File: "joins/a-b.m4a",
        OutgoingStopMs: 170_000,
        IncomingResumeMs: 12_000,
        DurationMs: 20_000
    )
    {
        Data = """{"bars":8}""",
    },
    ct
);
```

| field | meaning |
|---|---|
| `FromTrackId` | the track the file leads out of; it must be the track that plays |
| `ToTrackId` | the track the file leads into: the listener's next track, or `FromTrackId` itself for a file that stands in for a stretch inside the playing track |
| `File` | the audio file in the plugin's own folder (`context.Storage.Private`), by its relative path with forward slashes |
| `OutgoingStopMs` | where the first track hands over to the file |
| `IncomingResumeMs` | where the second track goes on after the file |
| `DurationMs` | the length of the file, 1 to 600000 |
| `Data` | a JSON object the server passes on to the players unread, at most 65536 characters; null for none |

A file that begins with the first track alone and ends with the second track
alone lets a player switch to it and away from it without a click.

| answer | meaning |
|---|---|
| `Accepted` | the server holds the offer |
| `NothingPlaying` | nothing plays for this listener |
| `NotThePlayingTrack` | `FromTrackId` is not the track that plays |
| `NotTheNextTrack` | `ToTrackId` is neither the next track nor the playing one |
| `OutsideTheTrack` | the stop or the resume position lies past the end of its track |
| `TooLate` | the playing track is at or past the stop, or the players were already told what comes next |

A call without the capabilities, or for a file that is not in the plugin's
own folder, throws `PluginRefusedException`. Values that cannot be (a negative
position, a length out of range, data that is not one JSON object) throw
`ArgumentException`.

### One offer per listener, and when it ends

The server keeps one offer per listener. A new offer replaces the one before,
`WithdrawTransitionAsync(userId)` takes the plugin's own offer back, and the
offer ends by itself when it no longer fits what plays:

| what happens | topic that says so |
|---|---|
| another track starts, which is also how a join into the next track ends | `music.playback.started` |
| a join back into the playing track was played | none of its own; `music.playback.seeked` when the track goes on two seconds or more from where the server's clock stood |
| playback stops | `music.playback.stopped` |
| the next track is no longer `ToTrackId`: the queue changed at its front, or repeat changed what comes next | `music.playback.queue.changed`, `music.playback.mode.changed` |
| another device takes over | `music.playback.device.changed` |
| the listener seeks to the stop or past it | `music.playback.seeked` |

A change further down the queue leaves the offer, and so does a seek to
before the stop. An offer that leads back into the playing track does not
depend on the next track, so a change of the queue leaves it too.

Offer after `music.playback.started`, and again after any of the topics above
while there is still time. The players need ten seconds: that long before the
stop they are sent the offer.

Once the players were sent an offer it cannot be replaced, and a withdrawal
no longer reaches them: a player that loaded the file plays it. When no
player has started it five seconds past its stop, the server lets the offer
go and a new one can be made.

### What the players are sent

Ten seconds before the playing track reaches `OutgoingStopMs` the music hub
sends the listener's clients a message of its own, `PrepareTransition`:

```json
{
  "next_item": { "id": "..." },
  "transition": {
    "url": "/api/v1/plugins/{pluginId}/media/{ticket}",
    "mime": "audio/mp4",
    "duration_ms": 20000,
    "outgoing_stop_ms": 170000,
    "incoming_resume_ms": 12000,
    "data": { "bars": 8 }
  },
  "timestamp": 1790000000000
}
```

- `next_item` is the track the file leads into, in the shape `PrepareCrossfade`
  sends it. It is the playing item itself for a join back into the track.
- `url` is relative to the server, is made for that one listener at that
  moment, and plays for nobody else. It is fetched like every plugin media
  link, with the listener's own sign-in.
- The player that sounds plays the track up to `outgoing_stop_ms`, switches to
  the file and sends `CrossfadeStartCommand(duration_ms)`; at the file's end
  it goes on with `next_item` at `incoming_resume_ms` and sends
  `CrossfadeCompleteAtCommand(next_item.id, incoming_resume_ms)`.
- A file that is not loaded in time is left out. `PrepareCrossfade` is still
  sent when and as it always was, so the crossfade of always happens then. A
  client that took the transition ignores `PrepareCrossfade` for that track,
  and a client that does not know `PrepareTransition` never sees a difference.
- After a seek back to more than ten seconds before the stop, the message is
  sent again when its moment comes. The newest one for a track replaces the
  one before.

Both messages are written down in `signalr-contracts.json` under `messages`.

## What a track tells a plugin

The topics and the queue carry track ids. `IPluginContext.Music`
(`IPluginMusicQuery`) turns ids into tracks: `GetTracksByIdAsync(ids)` for
the ones a plugin was just told about, `GetTracksAsync(libraryId, skip, take)`
to walk a library once and keep what it needs.

Since ABI 12.15 a `PluginTrack` says enough to group, order and pick tracks:

| member | meaning |
|---|---|
| `Album`, `Artist` | the name of one album and one artist, as before |
| `AlbumId` | the id of the album `Album` names; two tracks with the same one are neighbours on that album |
| `Albums` | every album the track is on (`PluginTrackAlbum`: id and name), the one `Album` names first, the rest by name |
| `Artists` | every artist of the track (`PluginTrackArtist`: id and name), the one `Artist` names first, the rest by name; the server keeps no order of its own for them |
| `Genres` | the names of the track's genres in alphabetical order; where the track has none, those of its albums |
| `Year` | the year of the track's own date, or else of the album `Album` names; null when neither is known |

The five new members are init-only with empty defaults, so a plugin built
against an older 12 constructs and reads a track as before. Two reads of an
unchanged track are equal: the lists compare by their items.
