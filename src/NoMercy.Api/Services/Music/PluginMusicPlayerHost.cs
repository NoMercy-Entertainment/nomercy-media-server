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

using Microsoft.Extensions.DependencyInjection;
using NoMercy.Api.DTOs.Music;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Users;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Player;

namespace NoMercy.Api.Services.Music;

/// <summary>
/// The music player's answer to a plugin: what plays for a listener, and the
/// listener's upcoming tracks put in another order.
/// <para>
/// A plugin arranges what the listener queued and nothing else. Every change
/// keeps the same tracks, each as often as it was queued.
/// </para>
/// </summary>
public sealed class PluginMusicPlayerHost(
    MusicPlayerStateManager stateManager,
    MusicPlaybackService playbackService,
    IServiceScopeFactory scopeFactory
) : IPluginMusicPlayerHost
{
    public PluginMusicPlayback? Playback(Guid userId)
    {
        if (
            !stateManager.TryGetValue(userId, out MusicPlayerState? state)
            || state.CurrentItem is not { } track
        )
            return null;

        string? list = state.CurrentList?.ToString();

        return new(
            userId,
            track.Id,
            state.DeviceId,
            state.PlayState,
            state.Time,
            [.. state.Playlist.Select(queued => queued.Id)],
            state.Repeat,
            state.Shuffle
        )
        {
            CurrentList = string.IsNullOrEmpty(list) ? null : list,
        };
    }

    public Task<PluginMusicQueueAnswer> MoveInQueueAsync(
        Guid userId,
        Guid trackId,
        int position,
        CancellationToken ct = default
    ) =>
        ArrangeAsync(
            userId,
            queue =>
            {
                int from = queue.FindIndex(queued => queued.Id == trackId);
                if (from < 0)
                    return null;

                List<PlaylistTrackDto> arranged = [.. queue];
                PlaylistTrackDto moved = arranged[from];
                arranged.RemoveAt(from);
                arranged.Insert(Math.Min(position, arranged.Count), moved);

                return arranged;
            },
            ct
        );

    public Task<PluginMusicQueueAnswer> ReorderQueueAsync(
        Guid userId,
        IReadOnlyList<Guid> order,
        CancellationToken ct = default
    ) => ArrangeAsync(userId, queue => InOrder(queue, order), ct);

    /// <summary>
    /// The queued tracks in the order of <paramref name="order" />, or null
    /// when it does not name exactly the tracks that are queued, each as often
    /// as it is queued.
    /// </summary>
    private static List<PlaylistTrackDto>? InOrder(
        List<PlaylistTrackDto> queue,
        IReadOnlyList<Guid> order
    )
    {
        if (order.Count != queue.Count)
            return null;

        Dictionary<Guid, Queue<PlaylistTrackDto>> queued = [];
        foreach (PlaylistTrackDto track in queue)
        {
            if (!queued.TryGetValue(track.Id, out Queue<PlaylistTrackDto>? same))
                queued[track.Id] = same = new();

            same.Enqueue(track);
        }

        List<PlaylistTrackDto> arranged = new(queue.Count);
        foreach (Guid id in order)
        {
            if (!queued.TryGetValue(id, out Queue<PlaylistTrackDto>? same) || same.Count == 0)
                return null;

            arranged.Add(same.Dequeue());
        }

        return arranged;
    }

    /// <summary>
    /// Puts a listener's upcoming tracks in the order <paramref name="arrange" />
    /// makes of them, and tells the listener's players. Under the lock the
    /// player's own tick takes, so the tick never reads a half-arranged queue.
    /// </summary>
    private async Task<PluginMusicQueueAnswer> ArrangeAsync(
        Guid userId,
        Func<List<PlaylistTrackDto>, List<PlaylistTrackDto>?> arrange,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();

        User? user = await ListenerAsync(userId);
        if (user is null)
            return PluginMusicQueueAnswer.NothingPlaying;

        PluginMusicQueueAnswer answer = PluginMusicQueueAnswer.NothingPlaying;

        await playbackService.WithStateLockAsync(
            userId,
            async () =>
            {
                if (
                    !stateManager.TryGetValue(userId, out MusicPlayerState? state)
                    || state.CurrentItem is null
                )
                    return;

                List<PlaylistTrackDto> queue = state.Playlist;
                List<PlaylistTrackDto>? arranged = arrange(queue);
                if (arranged is null)
                {
                    answer = PluginMusicQueueAnswer.NotInQueue;
                    return;
                }

                answer = PluginMusicQueueAnswer.Accepted;
                if (arranged.SequenceEqual(queue, ReferenceEqualityComparer.Instance))
                    return;

                if (
                    !ReferenceEquals(arranged[0], queue[0])
                    && MusicPlaybackService.NextTrackWasAnnounced(state)
                )
                {
                    answer = PluginMusicQueueAnswer.TooLate;
                    return;
                }

                // A new list, never a change inside the old one: a hub command
                // that runs at the same moment reads either the old order or
                // the new one, not a list that is being edited.
                state.Playlist = arranged;
                state.QueueVersion++;

                await playbackService.UpdatePlaybackState(user, state);
            }
        );

        return answer;
    }

    private async Task<User?> ListenerAsync(Guid userId)
    {
        if (!stateManager.HasState(userId))
            return null;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        return await scope
            .ServiceProvider.GetRequiredService<IUserRepository>()
            .GetByIdAsync(userId);
    }
}
