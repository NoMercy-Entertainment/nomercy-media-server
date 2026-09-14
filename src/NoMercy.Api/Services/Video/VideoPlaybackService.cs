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

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Playback;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.Api.Services.Video;

public class VideoPlaybackService
{
    private readonly VideoPlayerStateManager _stateManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClientMessenger _clientMessenger;
    private readonly IEventBus? _eventBus;
    private static int PlayerStateEventId => Interlocked.Increment(ref field);

    public VideoPlaybackService(
        VideoPlayerStateManager stateManager,
        IServiceScopeFactory scopeFactory,
        IClientMessenger clientMessenger,
        IEventBus? eventBus = null
    )
    {
        _stateManager = stateManager;
        _scopeFactory = scopeFactory;
        _clientMessenger = clientMessenger;
        _eventBus = eventBus;
    }

    /// <summary>
    /// When each user last had the continue-watching carousel invalidated, and on which item.
    /// </summary>
    private readonly ConcurrentDictionary<
        Guid,
        (string Item, DateTime At)
    > _lastContinueWatchingRefresh = new();

    /// <summary>
    /// How long the carousel is left alone between progress reports for the same item. Progress
    /// arrives about once a second per playing device and every report reached every connected
    /// client as a cache invalidation, so each one refetched the row once a second for the whole
    /// length of a film. The row shows a resume position, not a running clock; it does not need
    /// to be correct to the second on a device nobody is watching.
    /// </summary>
    private static readonly TimeSpan ContinueWatchingRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How close to the end counts as finished. Players report every few seconds, so
    /// the last report always lands short of the exact duration.
    /// </summary>
    private const int CompletionToleranceMs = 5_000;

    /// <summary>
    /// Takes the position a player reported and moves the session to match it.
    /// Whatever is rendering the video owns the clock: the server used to run its
    /// own 100ms counter here, which kept advancing (and writing watch progress)
    /// while paused and long after every client had closed.
    /// </summary>
    internal async Task ApplyClientProgress(User user, VideoPlayerState state, int timeMs)
    {
        state.Time = timeMs;

        await PublishProgressEventAsync(user.Id, state);
        await PublishContinueWatchingRefreshAsync(user.Id, state);

        int duration = state.CurrentItem?.Duration.ToMilliSeconds() ?? 0;
        if (duration > 0 && state.Time >= duration - CompletionToleranceMs)
            await HandleTrackCompletion(user, state);
    }

    private async Task HandleTrackCompletion(User user, VideoPlayerState state)
    {
        if (state.CurrentItem == null)
            return;

        int currentIndex = state.Playlist.IndexOf(state.CurrentItem);

        if (currentIndex + 1 == state.Playlist.Count)
        {
            await PublishCompletedEventAsync(user.Id, state);

            UpdateState(state, -1);

            await UpdatePlaybackState(user, state);

            _stateManager.RemoveState(user.Id);

            return;
        }

        UpdateState(state, currentIndex + 1);

        await UpdatePlaybackState(user, state);
    }

    public async Task UpdatePlaybackState(User user, VideoPlayerState? state)
    {
        if (state is not null)
            state.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        EventPayload<PlayerStateEventElement> payload = new()
        {
            Events =
            [
                new()
                {
                    Event = new() { EventId = PlayerStateEventId, State = state },
                    Source = "videoHub",
                    Type = VideoEventType.PlayerStateChanged,
                    User = user,
                },
            ],
        };

        await _clientMessenger.SendTo("VideoPlayerState", "videoHub", user.Id, payload);
    }

    private void UpdateState(VideoPlayerState state, int currentIndex)
    {
        if (currentIndex == -1)
        {
            state.PlayState = true;
            state.Time = 0;
            state.CurrentItem = null;
            state.Playlist.Clear();
            state.CurrentList = new("/home", UriKind.Relative);
            state.Actions = new()
            {
                Disallows = new()
                {
                    Next = true,
                    Previous = true,
                    Muting = true,
                    Pausing = true,
                    Resuming = true,
                    Seeking = true,
                    Stopping = true,
                },
            };
        }
        else if (currentIndex + 1 < state.Playlist.Count)
        {
            state.PlayState = true;
            state.Time = 0;
            state.CurrentItem = state.Playlist[currentIndex + 1];
        }
        else
        {
            state.PlayState = false;
            state.Time = 0;
            state.CurrentItem = null;
        }
    }

    internal async Task PublishStartedEventAsync(Guid userId, VideoPlayerState state)
    {
        IEventBus? bus = _eventBus;
        if (bus is null || state.CurrentItem is null)
            return;

        await bus.PublishAsync(
            new PlaybackStartedEvent
            {
                UserId = userId,
                MediaId = state.CurrentItem.TmdbId,
                MediaType = state.CurrentItem.PlaylistType,
                DeviceId = state.DeviceId,
            }
        );
    }

    private async Task PublishProgressEventAsync(Guid userId, VideoPlayerState state)
    {
        IEventBus? bus = _eventBus;
        if (bus is null || state.CurrentItem is null)
            return;

        int duration = state.CurrentItem.Duration.ToMilliSeconds();

        await bus.PublishAsync(
            new PlaybackProgressUpdatedEvent
            {
                UserId = userId,
                MediaId = state.CurrentItem.TmdbId,
                Position = TimeSpan.FromMilliseconds(state.Time),
                Duration = TimeSpan.FromMilliseconds(duration),
            }
        );
    }

    /// <summary>
    /// Tells clients the continue-watching carousel is stale. Watch progress is
    /// exactly what that row is built from, so without this the carousel only
    /// caught up on a full page load: a title started on one device kept showing
    /// its old position (or stayed missing entirely) on every other device.
    /// Mirrors the key TvShowsController already publishes after a watch toggle.
    /// </summary>
    private async Task PublishContinueWatchingRefreshAsync(Guid userId, VideoPlayerState state)
    {
        IEventBus? bus = _eventBus;
        if (bus is null)
            return;

        if (!ShouldRefreshContinueWatching(userId, state))
            return;

        await bus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["continue-watching"] });
    }

    /// <summary>
    /// True when the carousel has actually gone stale: a different title is being watched, or
    /// enough time has passed that the stored resume position is worth catching up on.
    /// </summary>
    private bool ShouldRefreshContinueWatching(Guid userId, VideoPlayerState state)
    {
        string item =
            $"{state.CurrentItem?.PlaylistType}:{state.CurrentItem?.TmdbId}:{state.CurrentItem?.VideoId}";
        DateTime now = DateTime.UtcNow;

        if (!_lastContinueWatchingRefresh.TryGetValue(userId, out (string Item, DateTime At) last))
        {
            _lastContinueWatchingRefresh[userId] = (item, now);
            return true;
        }

        bool itemChanged = !string.Equals(last.Item, item, StringComparison.Ordinal);
        bool intervalElapsed = now - last.At >= ContinueWatchingRefreshInterval;

        if (!itemChanged && !intervalElapsed)
            return false;

        _lastContinueWatchingRefresh[userId] = (item, now);
        return true;
    }

    private async Task PublishCompletedEventAsync(Guid userId, VideoPlayerState state)
    {
        IEventBus? bus = _eventBus;
        if (bus is null || state.CurrentItem is null)
            return;

        await bus.PublishAsync(
            new PlaybackCompletedEvent
            {
                UserId = userId,
                MediaId = state.CurrentItem.TmdbId,
                MediaType = state.CurrentItem.PlaylistType,
            }
        );
    }

    internal async Task StoreWatchProgression(VideoPlayerState state, User user)
    {
        if (state.CurrentItem is null || state.Time <= 0)
            return;

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IUserDataRepository userDataRepository =
            scope.ServiceProvider.GetRequiredService<IUserDataRepository>();

        // A rescan that runs mid-playback reinserts the VideoFile under a new id, so
        // the id captured at start can point at a row that is gone; the upsert skips it.
        bool stored = await userDataRepository.UpsertWatchProgressAsync(
            new(
                user.Id,
                state.CurrentItem.PlaylistType,
                Convert.ToString((object?)state.CurrentItem.PlaylistId) ?? string.Empty,
                state.CurrentItem.TmdbId,
                state.CurrentItem.VideoId,
                state.Time / 1000,
                null,
                null,
                null
            )
        );
        if (!stored)
        {
            Logger.App(
                $"StoreWatchProgression: nothing stored for {state.CurrentItem.PlaylistType} video file {state.CurrentItem.VideoId}",
                LogEventLevel.Warning
            );
            return;
        }

        await PublishContinueWatchingRefreshAsync(user.Id, state);
    }
}
