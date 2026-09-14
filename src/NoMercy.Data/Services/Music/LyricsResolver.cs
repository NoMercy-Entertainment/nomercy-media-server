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
using Newtonsoft.Json;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Music;
using NoMercy.Providers.Lyrics;

namespace NoMercy.Data.Services.Music;

/// <summary>
/// Coalesces concurrent lyric fetches per track. When several devices request
/// the same track's lyrics while none are cached, the provider fetch runs once
/// and every caller awaits that single result. Without this each device hits the
/// rate-limited Lrclib/Musixmatch queues independently, stacking the delay.
/// </summary>
public class LyricsResolver(IServiceScopeFactory scopeFactory, ILyricsAggregator lyricsAggregator)
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<Lyric[]?>>> _inFlight = new();

    // A provider hiccup (timeout / rate limit / unexpected error) is not a
    // confirmed "no lyrics" and must not be cached as one -- but replaying the
    // exact same slow chain on the very next play of the same track just to
    // hit the same hiccup again is wasted latency. This short backoff means a
    // track that just failed transiently returns null immediately for a
    // while, then gets a real attempt again once it expires.
    private readonly ConcurrentDictionary<Guid, DateTime> _transientBackoffUntil = new();
    private static readonly TimeSpan DefaultTransientBackoff = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _transientBackoff = DefaultTransientBackoff;

    // Test-only seam (NoMercy.Tests.Api has InternalsVisibleTo) so a backoff
    // expiry test can shrink the window instead of waiting out the real one.
    internal LyricsResolver(
        IServiceScopeFactory scopeFactory,
        ILyricsAggregator lyricsAggregator,
        TimeSpan transientBackoff
    )
        : this(scopeFactory, lyricsAggregator)
    {
        _transientBackoff = transientBackoff;
    }

    /// <summary>
    /// Returns the track's lyrics, fetching + persisting them once even when
    /// called concurrently for the same track. Returns null when no lyrics
    /// could be resolved (including while a prior transient failure is still
    /// within its backoff window).
    /// </summary>
    public Task<Lyric[]?> ResolveAsync(Guid trackId)
    {
        if (_transientBackoffUntil.TryGetValue(trackId, out DateTime until))
        {
            if (DateTime.UtcNow < until)
                return Task.FromResult<Lyric[]?>(null);
            _transientBackoffUntil.TryRemove(trackId, out _);
        }

        // Lazy guarantees the factory runs once even under GetOrAdd contention.
        return _inFlight.GetOrAdd(trackId, _ => new(() => FetchAndPersistAsync(trackId))).Value;
    }

    private async Task<Lyric[]?> FetchAndPersistAsync(Guid trackId)
    {
        try
        {
            // Own scope: the fetch outlives any single request, so a caller
            // disconnecting mid-flight can't dispose the context the shared
            // task depends on.
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IMusicRepository repository =
                scope.ServiceProvider.GetRequiredService<IMusicRepository>();

            Track? track = await repository.GetTrackWithIncludesAsync(trackId);
            if (track is null)
                return null;

            // A racing request may have persisted lyrics between the caller's
            // cache check and us acquiring the in-flight slot.
            // A non-empty array is a real cache hit. An empty array is the
            // negative marker persisted below: the track has been checked and
            // has no lyrics, so don't re-query the rate-limited providers.
            if (track.Lyrics is { Length: > 0 })
                return track.Lyrics;
            if (track.Lyrics is not null)
                return null;

            LyricsFetchResult result = await lyricsAggregator.SearchLyrics(track);

            if (result.IsTransientError)
            {
                _transientBackoffUntil[trackId] = DateTime.UtcNow.Add(_transientBackoff);
                return null;
            }

            _transientBackoffUntil.TryRemove(trackId, out _);

            if (result.Lines is null)
            {
                // Negative cache: record "checked, none found" so every later
                // play of this track doesn't re-hit Lrclib/Musixmatch and trip
                // their rate limits. A library rescan still overwrites this if
                // lyrics appear for the track later.
                await repository.UpdateTrackLyricsAsync(track, "[]");
                return null;
            }

            return await repository.UpdateTrackLyricsAsync(
                track,
                JsonConvert.SerializeObject(result.Lines)
            );
        }
        finally
        {
            _inFlight.TryRemove(trackId, out _);
        }
    }
}
