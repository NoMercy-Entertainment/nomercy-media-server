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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Database;
using NoMercy.MediaProcessing.Common;
using NoMercy.NmSystem.Extensions;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Shows;

/// <summary>
/// One-off backfill for libraries that already had anime imported before this
/// feature shipped. Always enqueues provider calls at low priority
/// (priority: false) through the shared rate-limited provider clients, so a
/// large backfill (potentially thousands of titles, serialized through
/// AniList's rate cap) never delays live per-item enrichment during a
/// concurrent library scan.
///
/// Drains in small, cursor-tracked batches and re-enqueues itself, mirroring
/// <c>PaletteBackfillJob</c>: a restart resumes at the last completed batch
/// instead of re-walking every anime title from the start.
///
/// A title the providers could not answer (an outage, a timeout, a rate
/// limit) is not done: its id is recorded and asked again on the next run, and
/// the completion flag is only set once nothing is left unanswered. The retry
/// happens in a later run, never the one that just failed, so a long outage
/// costs one attempt per title per boot instead of a tight loop.
///
/// Dispatched once, on boot, by <c>AnimeEnrichmentBackfillStartupService</c>
/// via the "extras" queue at the same priority as its ShowExtrasJob/
/// MovieExtrasJob siblings (not the queue's absolute floor - see
/// <see cref="Priority"/>), and re-checks the completion flag itself so a
/// redundant dispatch is a no-op.
/// </summary>
[Serializable]
public class AnimeEnrichmentBackfillJob : IShouldQueue, IJobStorageInjector
{
    public string QueueName => "extras";

    // Same tier as ShowExtrasJob/MovieExtrasJob (priority 1), not the queue's
    // absolute floor (0): a floor priority means every self-redispatch below
    // sorts this job strictly behind EVERY other extras-queue job, including
    // ones enqueued after it - on a live server the backlog never actually
    // empties, so priority 0 here means the backfill effectively never runs
    // again after its first batch. Priority 1 lets it interleave FIFO with
    // its siblings while still never contending with import/live queues,
    // which are separate queues entirely.
    public int Priority => 1;

    private const int BatchSize = 25;
    private const string TvType = "tv";
    private const string MovieType = "movie";

    private IAnimeEnrichmentService? _animeEnrichmentService;
    private IDbContextFactory<MediaContext>? _contextFactory;
    private IDbContextFactory<AppDbContext>? _appDbContextFactory;

    /// <summary>Used by tests and any direct caller that already has the service.</summary>
    public AnimeEnrichmentBackfillJob(IAnimeEnrichmentService animeEnrichmentService)
    {
        _animeEnrichmentService = animeEnrichmentService;
    }

    /// <summary>Required by the queue for deserialization; services are supplied via <see cref="InjectStorageServices"/>.</summary>
    public AnimeEnrichmentBackfillJob() { }

    // Purely a dedup-breaker: the queue drops a Dispatch() whose serialized
    // payload matches an already-present row (JobQueue.Enqueue's JobExists
    // check), and this job's own reserved row is STILL PRESENT while Handle()
    // runs - the worker only deletes it in a finally block after Handle()
    // returns (QueueWorker.cs). A parameterless AnimeEnrichmentBackfillJob()
    // therefore always collides with itself on self-redispatch: the insert
    // silently no-ops, the worker then deletes the old row, and the backfill
    // dies with zero trace - verified live, reproduced twice. Stamping the
    // cursor this batch just advanced to makes every redispatch's payload
    // provably different from the row it is dispatched from inside.
    public int? DispatchedAfterTvCursor { get; set; }
    public int? DispatchedAfterMovieCursor { get; set; }

    /// <summary>
    /// True once this run has walked new titles. Unanswered titles are then left
    /// for the next run: the providers just failed them, so asking again at once
    /// would only spend the rate limit on an outage.
    /// </summary>
    public bool SweptThisRun { get; set; }

    /// <summary>
    /// How far this run's retry of unanswered titles has got. Kept on the job,
    /// not stored, so every run starts its retry from the first unanswered id;
    /// it also breaks the dedup collision the cursors above describe.
    /// </summary>
    public int? RetriedAfterTvId { get; set; }
    public int? RetriedAfterMovieId { get; set; }

    public void InjectStorageServices(IServiceProvider serviceProvider)
    {
        _animeEnrichmentService ??= serviceProvider.GetRequiredService<IAnimeEnrichmentService>();
        _contextFactory ??= serviceProvider.GetRequiredService<IDbContextFactory<MediaContext>>();
        _appDbContextFactory ??= serviceProvider.GetRequiredService<
            IDbContextFactory<AppDbContext>
        >();
    }

    public async Task Handle()
    {
        AnimeEnrichmentBackfillJob? next = await RunBatchAsync();
        if (next is not null)
            QueueRunner.Current?.Dispatcher.Dispatch(next, "extras", Priority);
    }

    /// <summary>
    /// Runs one batch and returns the job that continues the run, or null when
    /// this run is over. Split from <see cref="Handle"/> so a test can drive the
    /// whole backfill without a live queue.
    /// </summary>
    internal async Task<AnimeEnrichmentBackfillJob?> RunBatchAsync()
    {
        if (
            _animeEnrichmentService is null
            || _contextFactory is null
            || _appDbContextFactory is null
        )
            return null;

        await using AppDbContext appDb = await _appDbContextFactory.CreateDbContextAsync();
        if (await AnimeEnrichmentBackfillState.IsCompleteAsync(appDb, CancellationToken.None))
            return null;

        await using MediaContext context = await _contextFactory.CreateDbContextAsync();

        int? tvCursor = await SweepBatchAsync(
            appDb,
            TvType,
            cursor => TvRows(context).Where(row => row.Id > cursor).Take(BatchSize).ToListAsync(),
            EnrichTvAsync
        );
        int? movieCursor = await SweepBatchAsync(
            appDb,
            MovieType,
            cursor =>
                MovieRows(context).Where(row => row.Id > cursor).Take(BatchSize).ToListAsync(),
            EnrichMovieAsync
        );

        if (tvCursor is not null || movieCursor is not null)
            return new()
            {
                DispatchedAfterTvCursor = tvCursor,
                DispatchedAfterMovieCursor = movieCursor,
                SweptThisRun = true,
            };

        if (!SweptThisRun)
        {
            int? tvRetried = await RetryBatchAsync(
                appDb,
                TvType,
                RetriedAfterTvId,
                ids => TvRows(context).Where(row => ids.Contains(row.Id)).ToListAsync(),
                EnrichTvAsync
            );
            int? movieRetried = await RetryBatchAsync(
                appDb,
                MovieType,
                RetriedAfterMovieId,
                ids => MovieRows(context).Where(row => ids.Contains(row.Id)).ToListAsync(),
                EnrichMovieAsync
            );

            // An exhausted side keeps its last position, so the next batch does
            // not start that side's retry over again.
            if (tvRetried is not null || movieRetried is not null)
                return new()
                {
                    RetriedAfterTvId = tvRetried ?? RetriedAfterTvId,
                    RetriedAfterMovieId = movieRetried ?? RetriedAfterMovieId,
                };
        }

        if (await HasUnansweredTitlesAsync(appDb))
            return null;

        await AnimeEnrichmentBackfillState.SetCompleteAsync(appDb, CancellationToken.None);
        return null;
    }

    // Walks the next batch past the stored cursor. Returns the batch's last id,
    // or null when there is nothing past the cursor - the return value doubles
    // as the follow-up dispatch's dedup-breaking stamp.
    private static async Task<int?> SweepBatchAsync(
        AppDbContext appDb,
        string entityType,
        Func<int, Task<List<BackfillRow>>> rowsAfter,
        Func<BackfillRow, Task<bool>> enrich
    )
    {
        int cursor = await AnimeEnrichmentBackfillState.GetCursorAsync(
            appDb,
            entityType,
            CancellationToken.None
        );

        List<BackfillRow> rows = await rowsAfter(cursor);
        if (rows.Count == 0)
            return null;

        List<int> unanswered = [];
        foreach (BackfillRow row in rows)
            if (!await enrich(row))
                unanswered.Add(row.Id);

        if (unanswered.Count > 0)
        {
            List<int> known = await AnimeEnrichmentBackfillState.GetUnansweredIdsAsync(
                appDb,
                entityType,
                CancellationToken.None
            );
            await AnimeEnrichmentBackfillState.SetUnansweredIdsAsync(
                appDb,
                entityType,
                [.. known, .. unanswered],
                CancellationToken.None
            );
        }

        await AnimeEnrichmentBackfillState.SetCursorAsync(
            appDb,
            entityType,
            rows[^1].Id,
            CancellationToken.None
        );
        return rows[^1].Id;
    }

    // Asks again for the next batch of unanswered titles past retriedAfter.
    // Returns the batch's last id, or null when this run's retry is through.
    private static async Task<int?> RetryBatchAsync(
        AppDbContext appDb,
        string entityType,
        int? retriedAfter,
        Func<List<int>, Task<List<BackfillRow>>> rowsWithIds,
        Func<BackfillRow, Task<bool>> enrich
    )
    {
        List<int> unanswered = await AnimeEnrichmentBackfillState.GetUnansweredIdsAsync(
            appDb,
            entityType,
            CancellationToken.None
        );
        List<int> batch = unanswered.Where(id => id > (retriedAfter ?? 0)).Take(BatchSize).ToList();
        if (batch.Count == 0)
            return null;

        // A title that was deleted, or has since been filed under anime, does
        // not come back from the query and so leaves the list: nothing is left
        // to ask about it.
        List<BackfillRow> rows = await rowsWithIds(batch);
        HashSet<int> stillUnanswered = [];
        foreach (BackfillRow row in rows)
            if (!await enrich(row))
                stillUnanswered.Add(row.Id);

        HashSet<int> tried = [.. batch];
        await AnimeEnrichmentBackfillState.SetUnansweredIdsAsync(
            appDb,
            entityType,
            unanswered.Where(id => !tried.Contains(id) || stillUnanswered.Contains(id)),
            CancellationToken.None
        );
        return batch[^1];
    }

    private static async Task<bool> HasUnansweredTitlesAsync(AppDbContext appDb)
    {
        List<int> tv = await AnimeEnrichmentBackfillState.GetUnansweredIdsAsync(
            appDb,
            TvType,
            CancellationToken.None
        );
        List<int> movies = await AnimeEnrichmentBackfillState.GetUnansweredIdsAsync(
            appDb,
            MovieType,
            CancellationToken.None
        );
        return tv.Count > 0 || movies.Count > 0;
    }

    private Task<bool> EnrichTvAsync(BackfillRow row) =>
        _animeEnrichmentService!.EnrichTvAsync(
            row.Id,
            row.Title,
            row.Date.ParseYear(),
            OriginCountries.Parse(row.OriginCountry),
            false
        );

    private Task<bool> EnrichMovieAsync(BackfillRow row) =>
        _animeEnrichmentService!.EnrichMovieAsync(
            row.Id,
            row.Title,
            row.Date.ParseYear(),
            OriginCountries.Parse(row.OriginCountry),
            false
        );

    // Every title outside an anime library is a backfill candidate, in id order.
    private static IQueryable<BackfillRow> TvRows(MediaContext context) =>
        context
            .Tvs.AsNoTracking()
            .Where(tv => tv.Library.Type != "anime")
            .OrderBy(tv => tv.Id)
            .Select(tv => new BackfillRow
            {
                Id = tv.Id,
                Title = tv.Title,
                Date = tv.FirstAirDate,
                OriginCountry = tv.OriginCountry,
            });

    private static IQueryable<BackfillRow> MovieRows(MediaContext context) =>
        context
            .Movies.AsNoTracking()
            .Where(movie => movie.Library.Type != "anime")
            .OrderBy(movie => movie.Id)
            .Select(movie => new BackfillRow
            {
                Id = movie.Id,
                Title = movie.Title,
                Date = movie.ReleaseDate,
                OriginCountry = movie.OriginCountry,
            });

    // A member-initialised class rather than a positional record: EF Core can
    // keep filtering (Where/Take) on a projection it can map member by member.
    private sealed class BackfillRow
    {
        public int Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public DateTime? Date { get; init; }
        public string? OriginCountry { get; init; }
    }
}
