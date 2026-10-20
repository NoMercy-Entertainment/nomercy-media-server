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

using System.Diagnostics;
using System.Globalization;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Lrclib.Client;
using NoMercy.Providers.Lrclib.Models;
using NoMercy.Providers.MusixMatch.Client;
using NoMercy.Providers.MusixMatch.Models;
using NoMercy.Providers.NoMercy.Client;
using NoMercy.Providers.NoMercy.Models;
using Serilog.Events;

namespace NoMercy.Providers.Lyrics;

public class LyricsAggregator : ILyricsAggregator
{
    // Bounds how long a single provider stage may keep the caller waiting.
    // Lrclib/Musixmatch normally answer in well under a second once their
    // rate-limited Queue lets the request through, so the default is generous.
    // A stage that blows the budget is NOT cancelled server-side -- its
    // HttpClient.Timeout / Queue retry chain keeps running in the background
    // so the provider's rate-limited slot is still released cleanly -- the
    // caller just stops waiting on it and the resolve treats it as an error.
    private static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(8);

    private readonly TimeSpan _providerTimeout;

    public LyricsAggregator()
        : this(DefaultProviderTimeout) { }

    // Test-only seam (NoMercy.Tests.Providers has InternalsVisibleTo) so a
    // timeout test can shrink the budget instead of waiting out the real one.
    internal LyricsAggregator(TimeSpan providerTimeout)
    {
        _providerTimeout = providerTimeout;
    }

    /// <summary>
    /// Resolves lyrics for a track, preferring the free Lrclib service and
    /// falling back to Musixmatch. Every candidate is validated against the
    /// track's title, artist and (for synced lyrics) release length, so a
    /// mismatched song or a wrong-release timing is rejected instead of stored.
    /// </summary>
    /// <summary>
    /// Every validated candidate of every stage, deduplicated by source and
    /// text, for a caller that chooses or combines the texts itself. The
    /// stages run concurrently; each provider's own rate limit still applies.
    /// </summary>
    public async Task<LyricsCandidatesResult> SearchCandidatesAsync(Track track)
    {
        using MusixmatchClient musixmatchClient = new();
        using LrclibClient lrclibClient = new();

        (LyricQuery query, string[] artists, string label) = QueryFor(track);
        Stopwatch total = Stopwatch.StartNew();

        ProviderAttempt[] single = await Task.WhenAll(
            RunProvider(
                "Lrclib-get",
                LyricsSources.Lrclib,
                () => FromLrclibGet(lrclibClient, query, artists)
            ),
            RunProvider(
                "Musixmatch-tight",
                LyricsSources.Musixmatch,
                () => MusixmatchTight(musixmatchClient, query)
            ),
            RunProvider(
                "Musixmatch-relaxed",
                LyricsSources.Musixmatch,
                () => MusixmatchRelaxed(musixmatchClient, query)
            )
        );
        ProviderAttempts search = await RunProviderMany(
            "Lrclib-search",
            LyricsSources.Lrclib,
            () => FromLrclibSearchAll(lrclibClient, query, artists)
        );

        List<LyricsTextCandidate> candidates = [];
        HashSet<string> seen = [];
        foreach (ProviderAttempt attempt in single)
            if (attempt.Candidate is not null)
                AddCandidate(candidates, seen, attempt.Source, attempt.Candidate);
        foreach (LyricCandidate candidate in search.Candidates)
            AddCandidate(candidates, seen, search.Source, candidate);

        bool errored = search.Errored || single.Any(attempt => attempt.Errored);
        Logger.Lyrics(
            $"{candidates.Count} lyric candidate(s) for {label} in {total.ElapsedMilliseconds}ms"
        );

        return candidates.Count == 0 && errored
            ? LyricsCandidatesResult.TransientFailure
            : new(candidates, false);
    }

    private static void AddCandidate(
        List<LyricsTextCandidate> candidates,
        HashSet<string> seen,
        string source,
        LyricCandidate candidate
    )
    {
        string key = source + "\n" + string.Join("\n", candidate.Lines.Select(line => line.Text));
        if (seen.Add(key))
            candidates.Add(new(source, candidate));
    }

    private static (LyricQuery Query, string[] Artists, string Label) QueryFor(Track track)
    {
        string[] artists = track
            .ArtistTrack.Select(artistTrack => artistTrack.Artist.Name)
            .ToArray();
        string? albumName = track.AlbumTrack.FirstOrDefault()?.Album.Name;
        int parsedDuration = track.Duration.ToSeconds();
        int? durationSeconds = parsedDuration > 0 ? parsedDuration : null;

        LyricQuery query = new(track.Name, artists, albumName, durationSeconds);
        return (query, artists, $"{track.Name} - {string.Join(", ", artists)}");
    }

    public async Task<LyricsFetchResult> SearchLyrics(Track track)
    {
        // `using` so an exception mid-search doesn't leak the HttpClient wrappers.
        using MusixmatchClient musixmatchClient = new();
        using LrclibClient lrclibClient = new();

        (LyricQuery query, string[] artists, string label) = QueryFor(track);
        Stopwatch total = Stopwatch.StartNew();

        // The /get endpoint is an exact artist+title+album+duration lookup.
        // When it yields a synced match that passes validation it's
        // authoritative, so skip the broader /search + Musixmatch calls
        // entirely: one provider round trip instead of up to four.
        ProviderAttempt exact = await RunProvider(
            "Lrclib-get",
            LyricsSources.Lrclib,
            () => FromLrclibGet(lrclibClient, query, artists)
        );
        if (exact.Candidate is { HasSyncedLyrics: true })
            return Resolved(label, exact, total.ElapsedMilliseconds);

        // /get missed or only had plain lyrics: race the fuzzy Lrclib search
        // against Musixmatch instead of running every remaining call one after
        // another. Racing never lets a wrong match through -- both branches
        // still validate every candidate they see, and PickBest below scores
        // everything that came back (plus a lingering plain /get hit) and only
        // ever returns the best-scoring, still-valid candidate.
        Task<ProviderAttempt> searchTask = RunProvider(
            "Lrclib-search",
            LyricsSources.Lrclib,
            () => FromLrclibSearch(lrclibClient, query, artists)
        );
        Task<ProviderAttempt> musixmatchTask = RunMusixmatch(musixmatchClient, query);
        ProviderAttempt[] raced = await Task.WhenAll(new[] { searchTask, musixmatchTask });

        ProviderAttempt[] attempts = [exact, raced[0], raced[1]];
        LyricCandidate? best = LyricMatcher.PickBest(
            query,
            attempts.Select(attempt => attempt.Candidate).OfType<LyricCandidate>()
        );

        total.Stop();

        if (best is not null)
        {
            ProviderAttempt winner = attempts.First(attempt =>
                ReferenceEquals(attempt.Candidate, best)
            );
            return Resolved(label, winner, total.ElapsedMilliseconds);
        }

        if (attempts.Any(attempt => attempt.Errored))
        {
            Logger.Lyrics(
                $"lyrics NOT resolved for {label} after {total.ElapsedMilliseconds}ms: a provider call failed or timed out, treating as transient",
                LogEventLevel.Warning
            );
            return LyricsFetchResult.TransientFailure;
        }

        Logger.Lyrics(
            $"lyrics NOT found for {label} after {total.ElapsedMilliseconds}ms, providers=[Lrclib-get, Lrclib-search, Musixmatch]"
        );
        return LyricsFetchResult.NotFound;
    }

    private static LyricsFetchResult Resolved(string label, ProviderAttempt winner, long elapsedMs)
    {
        LyricCandidate candidate = winner.Candidate!;
        Logger.Lyrics(
            $"lyrics resolved for {label} via {winner.Provider} in {elapsedMs}ms (synced={candidate.HasSyncedLyrics})"
        );
        return LyricsFetchResult.Found(
            candidate.Lines,
            winner.Provider,
            winner.Source,
            candidate.HasSyncedLyrics
        );
    }

    /// <summary>
    /// Runs one provider stage with a timing/outcome log line and a hard wall
    /// clock budget. A timeout or any other exception is reported as
    /// <see cref="ProviderAttempt.Errored"/> rather than propagated, so one bad
    /// provider stage never takes the whole resolve down with it.
    /// </summary>
    private async Task<ProviderAttempt> RunProvider(
        string provider,
        string source,
        Func<Task<LyricCandidate?>> fetch
    )
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            LyricCandidate? candidate = await fetch().WaitAsync(_providerTimeout);
            stopwatch.Stop();
            string outcome =
                candidate is null ? "miss"
                : candidate.HasSyncedLyrics ? "hit-synced"
                : "hit-plain";
            Logger.Lyrics($"{provider}: {outcome} in {stopwatch.ElapsedMilliseconds}ms");
            return new(provider, source, candidate, false);
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            Logger.Lyrics(
                $"{provider}: timed out after {stopwatch.ElapsedMilliseconds}ms (call keeps running in the background to release its rate-limit slot)",
                LogEventLevel.Warning
            );
            return new(provider, source, null, true);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.Lyrics(
                $"{provider}: error after {stopwatch.ElapsedMilliseconds}ms ({ex.GetType().Name}: {ex.Message})",
                LogEventLevel.Warning
            );
            return new(provider, source, null, true);
        }
    }

    private async Task<ProviderAttempt> RunMusixmatch(MusixmatchClient client, LyricQuery query)
    {
        // Tighten first (album + duration), then relax. A clean miss on the
        // tight query still tries the relaxed one; an error/timeout does not,
        // since a struggling provider is unlikely to answer the second call
        // any faster and it only extends the wait.
        ProviderAttempt tight = await RunProvider(
            "Musixmatch-tight",
            LyricsSources.Musixmatch,
            () => MusixmatchTight(client, query)
        );
        if (tight.Candidate is not null || tight.Errored)
            return tight;

        return await RunProvider(
            "Musixmatch-relaxed",
            LyricsSources.Musixmatch,
            () => MusixmatchRelaxed(client, query)
        );
    }

    /// <summary>
    /// <see cref="RunProvider" /> for a stage that keeps every validated
    /// candidate rather than its best one. Same budget, same outcome logging,
    /// same "an error is a miss, not a crash" rule.
    /// </summary>
    private async Task<ProviderAttempts> RunProviderMany(
        string provider,
        string source,
        Func<Task<IReadOnlyList<LyricCandidate>>> fetch
    )
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<LyricCandidate> candidates = await fetch().WaitAsync(_providerTimeout);
            stopwatch.Stop();
            Logger.Lyrics(
                $"{provider}: {candidates.Count} candidate(s) in {stopwatch.ElapsedMilliseconds}ms"
            );
            return new(source, candidates, false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.Lyrics(
                $"{provider}: {(ex is TimeoutException ? "timed out" : "error")} after {stopwatch.ElapsedMilliseconds}ms ({ex.GetType().Name}: {ex.Message})",
                LogEventLevel.Warning
            );
            return new(source, [], true);
        }
    }

    private static async Task<IReadOnlyList<LyricCandidate>> FromLrclibSearchAll(
        LrclibClient client,
        LyricQuery query,
        string[] artists
    )
    {
        LrclibSongResult[]? results = await client.Search(artists, query.Title);
        if (results is null)
            return [];

        List<LyricCandidate> validated = [];
        foreach (LrclibSongResult result in results)
            if (
                LrclibClient.ToCandidate(result) is { } candidate
                && LyricMatcher.Score(query, candidate) >= 0
            )
                validated.Add(candidate);

        return validated;
    }

    private static async Task<LyricCandidate?> FromLrclibGet(
        LrclibClient client,
        LyricQuery query,
        string[] artists
    )
    {
        LrclibSongResult? exact = await client.Get(
            artists,
            query.Title,
            query.Album,
            query.DurationSeconds
        );
        if (exact is null || LrclibClient.ToCandidate(exact) is not { } candidate)
            return null;

        return LyricMatcher.PickBest(query, [candidate]);
    }

    private static async Task<LyricCandidate?> FromLrclibSearch(
        LrclibClient client,
        LyricQuery query,
        string[] artists
    )
    {
        LrclibSongResult[]? results = await client.Search(artists, query.Title);
        if (results is null)
            return null;

        List<LyricCandidate> candidates = [];
        foreach (LrclibSongResult result in results)
            if (LrclibClient.ToCandidate(result) is { } candidate)
                candidates.Add(candidate);

        return LyricMatcher.PickBest(query, candidates);
    }

    private static async Task<LyricCandidate?> MusixmatchTight(
        MusixmatchClient client,
        LyricQuery query
    )
    {
        string artistNames = string.Join(",", query.Artists);
        string duration =
            query.DurationSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        return Validate(
            query,
            await client.SongSearch(
                new()
                {
                    Album = query.Album,
                    Artist = artistNames,
                    Title = query.Title,
                    Duration = duration,
                    Sort = MusixMatchTrackSearchParameters.MusixMatchSortStrategy.TrackRatingDesc,
                }
            )
        );
    }

    private static async Task<LyricCandidate?> MusixmatchRelaxed(
        MusixmatchClient client,
        LyricQuery query
    )
    {
        string artistNames = string.Join(",", query.Artists);

        return Validate(
            query,
            await client.SongSearch(
                new()
                {
                    Artist = artistNames,
                    Title = query.Title,
                    Sort = MusixMatchTrackSearchParameters.MusixMatchSortStrategy.TrackRatingDesc,
                }
            )
        );
    }

    private static LyricCandidate? Validate(LyricQuery query, MusixMatchSubtitleGet? response)
    {
        LyricCandidate? candidate = MusixMatchLyricMapper.ToCandidate(response);
        if (candidate is null)
            return null;
        return LyricMatcher.Score(query, candidate) >= 0 ? candidate : null;
    }

    private sealed record ProviderAttempt(
        string Provider,
        string Source,
        LyricCandidate? Candidate,
        bool Errored
    );

    private sealed record ProviderAttempts(
        string Source,
        IReadOnlyList<LyricCandidate> Candidates,
        bool Errored
    );
}
