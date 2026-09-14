// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using Newtonsoft.Json;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Hubs.Shared;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Services.Music;

public class MusicPlayerState
{
    [JsonProperty("actions")]
    public Actions Actions { get; set; } = new();

    [JsonProperty("device_id")]
    public string? DeviceId { get; set; }

    [JsonProperty("is_playing")]
    public bool PlayState { get; set; }

    [JsonProperty("item")]
    public PlaylistTrackDto? CurrentItem { get; set; }

    [JsonProperty("playlist")]
    public List<PlaylistTrackDto> Playlist { get; set; } = [];

    [JsonProperty("backlog")]
    public List<PlaylistTrackDto> Backlog { get; set; } = [];

    [JsonProperty("current_list")]
    public Uri CurrentList { get; set; } = null!;

    [JsonProperty("progress_ms")]
    public int Time { get; set; }

    /// <summary>
    /// The current track's length, derived rather than stored.
    ///
    /// It used to be a settable field kept in step by hand, and six call sites
    /// remembered while every auto-advance in
    /// <see cref="MusicPlaybackService"/> did not: those set
    /// <see cref="CurrentItem"/> and <see cref="SetPosition"/>(0) and left
    /// duration_ms describing the track before it. Clients that read the field
    /// showed the wrong length for the rest of the session — the web player
    /// reported a full scrubber and 00:00 remaining while the Android clients,
    /// which parse the item's own duration instead, were correct on the same
    /// broadcast. Reading it off the item is the one thing that cannot drift
    /// out of step with the item.
    /// </summary>
    [JsonProperty("duration_ms")]
    public int Duration => CurrentItem?.Duration.ToMilliSeconds() ?? 0;

    [JsonProperty("repeat_state")]
    public string Repeat { get; set; } = "off";

    [JsonProperty("shuffle_state")]
    public bool Shuffle { get; set; }

    [JsonProperty("muted_state")]
    public bool Muted { get; set; }

    [JsonProperty("timestamp")]
    public long Timestamp { get; set; }

    // Epoch ms the server clock read at the instant this broadcast left
    // MusicPlaybackService.UpdatePlaybackState. Distinct from Timestamp (which
    // predates this field and some clients may already depend on for other
    // purposes): this is the reference instant for GetServerTime-style clock-
    // offset math, letting every device compute the shared server clock and
    // therefore the same playback position regardless of its own wall-clock skew.
    [JsonProperty("server_time_ms")]
    public long ServerTimeMs { get; set; }

    // Epoch ms the server clock read at the instant Time was last authored —
    // an accepted position report, a seek, a device transfer, or a track
    // change — never on the internal 100ms playback tick (which advances Time
    // in lockstep with wall time and needs no fresh reference point). Paired
    // with Time and ServerTimeMs, a client derives the live position via
    // Time + (serverNow - PositionCapturedAtMs) without waiting on its own
    // report cadence. Old clients that ignore this field are unaffected.
    [JsonProperty("position_captured_at_ms")]
    public long PositionCapturedAtMs { get; set; }

    // Per-user monotonic, clock-anchored broadcast sequence — assigned once per
    // emit by MusicPlaybackService.UpdatePlaybackState via
    // MusicPlayerStateManager.NextSeq, never authored here. Lets a client that
    // receives broadcasts out of order (many ungated call sites can all fire
    // within milliseconds of each other) drop any state whose Seq is not
    // greater than the last one it applied, instead of racing position/
    // play-state fields against each other. New additive field: old clients
    // that don't read it are unaffected.
    [JsonProperty("seq")]
    public long Seq { get; set; }

    [JsonProperty("volume_percentage")]
    public int VolumePercentage { get; set; }

    // Case-insensitive: device ids are compared OrdinalIgnoreCase everywhere else in
    // MusicHub, and this map is keyed by the same ids (ResolveTransferVolume,
    // ApplyDeviceVolume). A case-sensitive map would silently miss a remembered
    // volume the moment a caller's casing didn't match the one that first wrote it.
    [JsonProperty("device_volumes")]
    public Dictionary<string, int> DeviceVolumes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonProperty("seek_offset")]
    public int SeekOffset { get; set; }

    [JsonIgnore]
    public DateTime IgnoreCurrentTimeUntil { get; set; }

    [JsonIgnore]
    public bool CrossfadeSignalSent { get; set; }

    // Set to true by CrossfadeStartCommand; suppresses server auto-advance during the
    // client's crossfade window.  Cleared by CrossfadeCompleteCommand or the safety timeout.
    [JsonIgnore]
    public bool IsCrossfading { get; set; }

    // The UTC deadline by which CrossfadeComplete must arrive before the server forces advance.
    // Equals DateTime.UtcNow + fadeDuration + CrossfadeSafetyMarginMs at the time CrossfadeStart
    // is received.
    [JsonIgnore]
    public DateTime CrossfadeTimeout { get; set; }

    // The DeviceId that sent CrossfadeStart.  Only that device may send CrossfadeComplete or
    // cancel the suppression, preventing multi-device conflicts.
    [JsonIgnore]
    public string? CrossfadeDeviceId { get; set; }

    // Proof-of-life clock for the active device (DeviceId above). Refreshed by
    // MusicHub.ReportPositionCommand while playing and by
    // MusicPlaybackService.StartPlaybackTimer on every (re)start of the ticking
    // loop (a resume after a long pause must not look instantly stale). Defaults
    // to "now" so a freshly-created session gets a full grace window before its
    // first position report is due. See MusicPlaybackService.IsActiveDeviceStale.
    [JsonIgnore]
    public DateTime LastActiveHeartbeatUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the device currently named in <see cref="DeviceId"/> has proven
    /// life even once since it was named. Cleared by a device change, set by the
    /// first heartbeat from that device.
    ///
    /// A device that has reported and then gone quiet is a device that died, and
    /// the short timeout should find it. A device that has never reported yet may
    /// simply not be up: a TV woken from sleep by Cast takes longer to boot,
    /// connect and start playing than the short timeout allows, and ending its
    /// session on that basis stops music that was seconds from playing. Measured
    /// on a Nokia Streaming Box: handed playback at 11:24:46, first sample at
    /// 11:25:04, session force-ended at 11:25:01. See
    /// <see cref="MusicPlaybackService.IsActiveDeviceStale"/>.
    /// </summary>
    [JsonIgnore]
    public bool ActiveDeviceProvenAlive { get; set; }

    /// <summary>
    /// The single choke point for authoring Time: sets the position and stamps
    /// <see cref="PositionCapturedAtMs"/> to server-now in the same call, so the
    /// two can never travel out of sync. Every seek, track change, and accepted
    /// position report must go through this rather than assigning
    /// <see cref="Time"/> directly.
    /// </summary>
    /// <param name="capturedAtMs">
    /// Server-clock instant the reporting device actually read this position, when it knows
    /// it. Stamping on arrival instead makes the stored position older than it claims by
    /// however long the report took to get here, and every passive device inherits that lag
    /// because it anchors its own interpolation to this instant. Zero or a value in the
    /// future falls back to now, so a client that does not send one behaves as before.
    /// </param>
    public void SetPosition(int positionMs, long capturedAtMs = 0)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Time = positionMs;
        PositionCapturedAtMs = capturedAtMs > 0 && capturedAtMs <= nowMs ? capturedAtMs : nowMs;
    }

    // The broadcast carries only a window of the queue, not the whole thing.
    // Playlist is the UPCOMING queue (front = the very next track), Backlog is
    // history (back = most recent) — see MusicPlaybackCommandHandler's advance
    // logic — so a front-of-Playlist / back-of-Backlog slice is exactly the
    // "up next" + "just played" a client renders. The server keeps the FULL
    // lists in its own state for auto-advance and previous; only the wire copy
    // is bounded. A genre queue is ~8k tracks: serializing all of them into
    // every ~5s position broadcast cost ~2s per emit server-side, which landed
    // each play/pause broadcast seconds late and let an interleaved stale tick
    // snap the client's play-state and position backward (the measured "pause
    // skips back and resumes"). The full list stays available on the REST
    // /music/{type}/{id} endpoint the client already loads its list screens from.
    private const int BroadcastPlaylistWindow = 100;
    private const int BroadcastBacklogWindow = 20;

    /// <summary>
    /// A lightweight serialization projection for the client broadcast. Two
    /// reductions, both keyed off the fact that a whole-genre queue is thousands
    /// of tracks re-sent on every ~5s position broadcast:
    /// <list type="number">
    /// <item>The queue is windowed — <see cref="Playlist"/> to the next
    /// <see cref="BroadcastPlaylistWindow"/> upcoming tracks and
    /// <see cref="Backlog"/> to the last <see cref="BroadcastBacklogWindow"/> —
    /// which is what caps the per-emit serialize cost (and thus remote-action
    /// latency).</item>
    /// <item>Every windowed entry drops the fields no client renders from a
    /// queue row — the track <see cref="PlaylistTrackDto.Lyrics"/> sheet, the
    /// track <see cref="PlaylistTrackDto.ColorPalette"/> swatch graph, and the
    /// same palette plus the unbounded description on each nested
    /// <see cref="PlaylistTrackDto.Album"/> / <see cref="PlaylistTrackDto.Artist"/>
    /// entry — which is what curbs a memory-tight client's GC thrash.</item>
    /// </list>
    /// A queue row shows only cover, name, and album/artist names; the current
    /// track's own theming comes from <see cref="CurrentItem"/> (kept whole) and
    /// each list screen fetches its palette from REST. The stored state is never
    /// mutated — this returns a throwaway shallow copy whose queue lists hold
    /// stripped record copies (so a queue entry that shares a reference with
    /// <see cref="CurrentItem"/> cannot strip it), and the nested album/artist
    /// copies are fresh clones so their in-place null-outs never reach the
    /// stored DTOs.
    /// </summary>
    public MusicPlayerState CloneForBroadcast()
    {
        MusicPlayerState copy = (MusicPlayerState)MemberwiseClone();
        copy.Playlist = [.. Playlist.Take(BroadcastPlaylistWindow).Select(StripQueueEntry)];
        copy.Backlog = [.. Backlog.TakeLast(BroadcastBacklogWindow).Select(StripQueueEntry)];
        return copy;
    }

    private static PlaylistTrackDto StripQueueEntry(PlaylistTrackDto track) =>
        track with
        {
            Lyrics = null,
            ColorPalette = null,
            Album = [.. track.Album.Select(album => album.ForBroadcastQueueEntry())],
            Artist = [.. track.Artist.Select(artist => artist.ForBroadcastQueueEntry())],
        };

    /// <summary>Nothing is playing: no current item, at the start, paused.</summary>
    public void Stop()
    {
        PlayState = false;
        SetPosition(0);
        CurrentItem = null;
    }

    /// <summary>
    /// Jumps ahead to the queued track at <paramref name="playlistIndex"/>: the current
    /// track and every track skipped over go to the backlog, and <paramref name="track"/>
    /// starts from the beginning.
    /// </summary>
    public void SkipTo(int playlistIndex, PlaylistTrackDto track)
    {
        if (CurrentItem != null)
            Backlog.Add(CurrentItem);

        Backlog.AddRange(Playlist.Take(playlistIndex));
        Playlist.RemoveRange(0, playlistIndex + 1);

        CurrentItem = track;
        SetPosition(0);
        IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
        PlayState = true;
    }

    /// <summary>
    /// Repeat-all wrap: the played tracks become the queue again and the first one
    /// starts. Stops when there is nothing to wrap to. Returns true when a track starts.
    /// </summary>
    public bool WrapBacklogIntoPlaylist()
    {
        Playlist = [.. Backlog];
        Backlog.Clear();

        if (Playlist.Count == 0)
        {
            Stop();
            return false;
        }

        CurrentItem = Playlist.First();
        Playlist.RemoveAt(0);
        SetPosition(0);
        PlayState = true;
        return true;
    }
}
