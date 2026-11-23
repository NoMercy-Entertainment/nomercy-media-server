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

using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Music;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.Services.Music;

/// <summary>
/// Tells plugins what a listener plays, on the <c>music.playback</c> topics of
/// <see cref="PluginTopics" />.
/// <para>
/// It is shown every state the music player is about to broadcast and works
/// out what changed since the one before. The player changes its state in
/// many places (hub commands, the 100 ms tick, a device that leaves), and all
/// of them end in that one broadcast, so nothing has to remember to announce.
/// </para>
/// <para>
/// A plugin's handler never runs on the player's own path: what changed is put
/// on a queue and published from there, in order. A handler that hangs holds
/// up the plugins, not the music.
/// </para>
/// </summary>
public sealed class MusicPlaybackAnnouncer : IDisposable
{
    /// <summary>
    /// The smallest jump in position that is announced as a seek. A player
    /// corrects the server's clock a little with every position report, and
    /// those steps are not jumps.
    /// </summary>
    internal const int SeekThresholdMs = 2000;

    // More than a busy server queues in normal use. Past it the oldest
    // announcement goes, so a handler that never returns cannot grow this
    // without end.
    private const int PendingLimit = 1024;

    private readonly IEventBus _eventBus;
    private readonly ILogger<MusicPlaybackAnnouncer>? _logger;
    private readonly Dictionary<Guid, Heard> _heard = [];
    private readonly Lock _gate = new();
    private readonly Channel<Func<PluginMessageEvent>> _pending = Channel.CreateBounded<
        Func<PluginMessageEvent>
    >(
        new BoundedChannelOptions(PendingLimit)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        }
    );

    public MusicPlaybackAnnouncer(
        IEventBus eventBus,
        ILogger<MusicPlaybackAnnouncer>? logger = null
    )
    {
        _eventBus = eventBus;
        _logger = logger;
        _ = Task.Run(PublishPendingAsync);
    }

    /// <summary>
    /// Shown a listener's state as it is about to be broadcast, or null when
    /// they have none. Never throws and never waits on a plugin.
    /// </summary>
    public void Observe(Guid userId, MusicPlayerState? state)
    {
        try
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (Announcement announcement in Changes(userId, state, nowMs))
            {
                _pending.Writer.TryWrite(announcement.ToEvent);
            }
        }
        catch (Exception ex)
        {
            // The queue is read while another thread may be changing it. A
            // missed announcement is repaired by the next broadcast; a throw
            // here would take the broadcast down with it.
            _logger?.LogDebug(
                ex,
                "Music playback for user {UserId} could not be announced to plugins",
                userId
            );
        }
    }

    /// <summary>
    /// The track that plays reached its end. What plays next is a start, also
    /// when it is the same track again (repeat one).
    /// </summary>
    public void TrackEnded(Guid userId)
    {
        lock (_gate)
        {
            if (_heard.TryGetValue(userId, out Heard? heard))
                heard.Ended = true;
        }
    }

    /// <summary>The listener has left; nothing is announced for it.</summary>
    public void Forget(Guid userId)
    {
        lock (_gate)
        {
            _heard.Remove(userId);
        }
    }

    public void Dispose() => _pending.Writer.TryComplete();

    /// <summary>
    /// What changed for a listener since the state shown before, as the
    /// announcements to make, and remembers this state for the next call.
    /// </summary>
    internal IReadOnlyList<Announcement> Changes(Guid userId, MusicPlayerState? state, long nowMs)
    {
        PlaylistTrackDto? track = state?.CurrentItem;

        lock (_gate)
        {
            if (state is null || track is null)
            {
                return _heard.Remove(userId)
                    ?
                    [
                        Announcement.Of(
                            PluginTopics.MusicPlaybackStopped,
                            new PluginMusicPlaybackStopped(userId)
                        ),
                    ]
                    : [];
            }

            Guid[] queue = QueueOf(state);
            int queueHash = HashOf(queue);
            List<Announcement> changes = [];

            if (
                !_heard.TryGetValue(userId, out Heard? heard)
                || heard.TrackId != track.Id
                || heard.Ended
            )
            {
                changes.Add(
                    Announcement.Of(
                        PluginTopics.MusicPlaybackStarted,
                        new PluginMusicPlaybackStarted(
                            userId,
                            track.Id,
                            state.DeviceId,
                            state.Time,
                            queue,
                            state.Repeat,
                            state.Shuffle
                        )
                        {
                            CurrentList = ListOf(state),
                        }
                    )
                );

                if (!state.PlayState)
                    changes.Add(Position(PluginTopics.MusicPlaybackPaused, userId, state));
            }
            else
            {
                int expectedMs = heard.PositionMs + (heard.Playing ? (int)(nowMs - heard.AtMs) : 0);

                if (heard.Playing != state.PlayState)
                    changes.Add(
                        Position(
                            state.PlayState
                                ? PluginTopics.MusicPlaybackResumed
                                : PluginTopics.MusicPlaybackPaused,
                            userId,
                            state
                        )
                    );
                else if (Math.Abs(state.Time - expectedMs) >= SeekThresholdMs)
                    changes.Add(Position(PluginTopics.MusicPlaybackSeeked, userId, state));

                if (!SameDevice(heard.DeviceId, state.DeviceId))
                    changes.Add(
                        Announcement.Of(
                            PluginTopics.MusicPlaybackDeviceChanged,
                            new PluginMusicPlaybackDeviceChanged(userId, state.DeviceId)
                        )
                    );

                if (heard.QueueCount != queue.Length || heard.QueueHash != queueHash)
                    changes.Add(
                        Announcement.Of(
                            PluginTopics.MusicPlaybackQueueChanged,
                            new PluginMusicPlaybackQueueChanged(userId, track.Id, queue)
                        )
                    );

                if (heard.Repeat != state.Repeat || heard.Shuffle != state.Shuffle)
                    changes.Add(
                        Announcement.Of(
                            PluginTopics.MusicPlaybackModeChanged,
                            new PluginMusicPlaybackModeChanged(
                                userId,
                                track.Id,
                                state.Repeat,
                                state.Shuffle
                            )
                        )
                    );
            }

            _heard[userId] = new()
            {
                TrackId = track.Id,
                Playing = state.PlayState,
                DeviceId = state.DeviceId,
                PositionMs = state.Time,
                AtMs = nowMs,
                QueueCount = queue.Length,
                QueueHash = queueHash,
                Repeat = state.Repeat,
                Shuffle = state.Shuffle,
            };

            return changes;
        }
    }

    private async Task PublishPendingAsync()
    {
        await foreach (Func<PluginMessageEvent> toEvent in _pending.Reader.ReadAllAsync())
        {
            try
            {
                await _eventBus.PublishAsync(toEvent());
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "A music playback announcement could not be published");
            }
        }
    }

    private static Announcement Position(string topic, Guid userId, MusicPlayerState state) =>
        Announcement.Of(
            topic,
            new PluginMusicPlaybackPosition(userId, state.CurrentItem!.Id, state.Time)
        );

    private static Guid[] QueueOf(MusicPlayerState state)
    {
        List<PlaylistTrackDto> playlist = state.Playlist;
        Guid[] queue = new Guid[playlist.Count];

        for (int index = 0; index < queue.Length; index++)
            queue[index] = playlist[index].Id;

        return queue;
    }

    private static int HashOf(Guid[] queue)
    {
        HashCode hash = new();

        foreach (Guid id in queue)
            hash.Add(id);

        return hash.ToHashCode();
    }

    private static string? ListOf(MusicPlayerState state)
    {
        string? list = state.CurrentList?.ToString();

        return string.IsNullOrEmpty(list) ? null : list;
    }

    private static bool SameDevice(string? one, string? other) =>
        string.Equals(one ?? "", other ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>One thing to tell plugins: a topic and what it carries.</summary>
    internal readonly record struct Announcement(
        string Topic,
        object Payload,
        Func<PluginMessageEvent> ToEvent
    )
    {
        // Sender Ulid.Empty: raised by the host. The plugin events facade
        // delivers a host topic only under that sender.
        public static Announcement Of<T>(string topic, T payload)
            where T : notnull =>
            new(
                topic,
                payload,
                () => PluginMessageEvent.From(Ulid.Empty, topic, payload, JsonSerializerOptions.Web)
            );
    }

    /// <summary>The state last shown for one listener.</summary>
    private sealed class Heard
    {
        public Guid TrackId { get; init; }
        public bool Playing { get; init; }
        public string? DeviceId { get; init; }
        public int PositionMs { get; init; }
        public long AtMs { get; init; }
        public int QueueCount { get; init; }
        public int QueueHash { get; init; }
        public string Repeat { get; init; } = "off";
        public bool Shuffle { get; init; }
        public bool Ended { get; set; }
    }
}
