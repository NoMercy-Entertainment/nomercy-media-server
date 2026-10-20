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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.Lyrics;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Providers.Lyrics;
using NoMercy.Storage;

namespace NoMercy.Data.Services.Music;

/// <summary>
/// Every lyric text the host can find for a track: each validated provider
/// candidate and the lyrics embedded in the file's tags, as text only.
/// <para>
/// A plugin may ask as often as it likes, so the answer is kept for a day per
/// track, and a provider hiccup backs off for two minutes rather than being
/// retried on the very next call, the same two rules <see cref="LyricsResolver" />
/// applies to the winner. Concurrent asks for one track share one fetch.
/// </para>
/// </summary>
public class LyricsTextCandidates(
    IDbContextFactory<MediaContext> contextFactory,
    ILyricsAggregator aggregator,
    IEmbeddedLyricsReader embeddedLyrics,
    IStorageDriver storageDriver,
    ILogger<LyricsTextCandidates>? logger = null
) : ILyricsTextCandidates
{
    private static readonly TimeSpan DefaultCacheWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultTransientBackoff = TimeSpan.FromMinutes(2);
    private static readonly PluginLyricsText Nothing = new([]);

    private readonly ConcurrentDictionary<Guid, Lazy<Task<PluginLyricsText>>> _inFlight = new();
    private readonly ConcurrentDictionary<Guid, (DateTime At, PluginLyricsText Text)> _cache =
        new();
    private readonly ConcurrentDictionary<Guid, DateTime> _transientBackoffUntil = new();
    private readonly TimeSpan _cacheWindow = DefaultCacheWindow;
    private readonly TimeSpan _transientBackoff = DefaultTransientBackoff;
    private readonly ILogger<LyricsTextCandidates> _logger =
        logger ?? NullLogger<LyricsTextCandidates>.Instance;

    // Test-only seam (NoMercy.Tests.Repositories has InternalsVisibleTo) so a
    // test can shrink the windows instead of waiting them out.
    internal LyricsTextCandidates(
        IDbContextFactory<MediaContext> contextFactory,
        ILyricsAggregator aggregator,
        IEmbeddedLyricsReader embeddedLyrics,
        IStorageDriver storageDriver,
        TimeSpan cacheWindow,
        TimeSpan transientBackoff
    )
        : this(contextFactory, aggregator, embeddedLyrics, storageDriver)
    {
        _cacheWindow = cacheWindow;
        _transientBackoff = transientBackoff;
    }

    public Task<PluginLyricsText> GetAsync(Guid trackId, CancellationToken ct = default)
    {
        if (
            _cache.TryGetValue(trackId, out (DateTime At, PluginLyricsText Text) cached)
            && DateTime.UtcNow - cached.At < _cacheWindow
        )
            return Task.FromResult(cached.Text);

        if (_transientBackoffUntil.TryGetValue(trackId, out DateTime until))
        {
            if (DateTime.UtcNow < until)
                return Task.FromResult(Nothing);
            _transientBackoffUntil.TryRemove(trackId, out _);
        }

        return _inFlight.GetOrAdd(trackId, _ => new(() => FetchAsync(trackId, ct))).Value;
    }

    private async Task<PluginLyricsText> FetchAsync(Guid trackId, CancellationToken ct)
    {
        try
        {
            await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
            Track? track = await context
                .Tracks.AsNoTracking()
                .Include(t => t.ArtistTrack)
                    .ThenInclude(at => at.Artist)
                .Include(t => t.AlbumTrack)
                    .ThenInclude(at => at.Album)
                .FirstOrDefaultAsync(t => t.Id == trackId, ct);
            if (track is null)
                return Nothing;

            List<PluginTextCandidate> candidates = [];

            EmbeddedLyrics? embedded = await ReadEmbeddedAsync(track, ct);
            if (embedded is not null)
                candidates.Add(
                    new(
                        LyricsSources.Embedded,
                        embedded.Lines,
                        embedded.Language,
                        embedded.WasSynced
                    )
                );

            LyricsCandidatesResult providers = await aggregator.SearchCandidatesAsync(track);
            foreach (LyricsTextCandidate candidate in providers.Candidates)
                candidates.Add(
                    new(
                        candidate.Source,
                        candidate.Candidate.Lines.Select(line => line.Text).ToList(),
                        null,
                        candidate.Candidate.HasSyncedLyrics
                    )
                );

            PluginLyricsText text = new(candidates);

            // A provider that failed is not "no text anywhere": the embedded
            // lyrics, if any, are still handed back, but the answer is not
            // kept for a day, only held off for the backoff.
            if (providers.IsTransientError)
            {
                _transientBackoffUntil[trackId] = DateTime.UtcNow.Add(_transientBackoff);
                return text;
            }

            _transientBackoffUntil.TryRemove(trackId, out _);
            _cache[trackId] = (DateTime.UtcNow, text);
            return text;
        }
        finally
        {
            _inFlight.TryRemove(trackId, out _);
        }
    }

    private async Task<EmbeddedLyrics?> ReadEmbeddedAsync(Track track, CancellationToken ct)
    {
        if (
            string.IsNullOrWhiteSpace(track.HostFolder) || string.IsNullOrWhiteSpace(track.Filename)
        )
            return null;

        try
        {
            return await embeddedLyrics.ReadAsync(
                storageDriver.CombinePath(track.HostFolder, track.Filename),
                ct
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Reading the embedded lyrics of track {TrackId} failed",
                track.Id
            );
            return null;
        }
    }
}
