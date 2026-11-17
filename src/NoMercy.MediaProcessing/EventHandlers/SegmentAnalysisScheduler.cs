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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Encoder.Composition;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Jobs.SegmentJobs;
using NoMercy.MediaProcessing.Segments;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.EventHandlers;

/// <summary>
/// Queues the intro / credits / end-of-content analysis (#598). It replaces the
/// chromaprint detector's two triggers: after an encode the item's season (or
/// the movie) is analysed again; after a library scan every season and movie of
/// the library that was never analysed is queued (the backfill), except items
/// that failed <see cref="ISegmentAnalysisState.MaxFailures"/> times. A new
/// encode clears those failures: the item's files changed. The queue
/// keeps one copy of an identical pending job, and the analysis job sits below
/// encodes on the encoder queue, so a season encoded in one batch is analysed
/// once, after its last episode.
/// <para>Opt-out: <see cref="EncoderOptions.EnableIntroDetectSubscriber"/> = false.</para>
/// </summary>
public sealed class SegmentAnalysisScheduler(
    IEventBus eventBus,
    IDbContextFactory<MediaContext> contextFactory,
    IJobDispatcher dispatcher,
    ISegmentAnalysisState state,
    EncoderOptions options,
    ILogger<SegmentAnalysisScheduler> logger
) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.EnableIntroDetectSubscriber)
        {
            logger.LogInformation("Segment analysis is switched off");
            return Task.CompletedTask;
        }

        _subscriptions.Add(eventBus.Subscribe<EncodingCompletedEvent>(OnEncodingCompletedAsync));
        _subscriptions.Add(eventBus.Subscribe<LibraryScanCompletedEvent>(OnLibraryScanCompletedAsync));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (IDisposable subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The encode's id names an episode or a movie (the event does not say
    /// which); both are looked up, and an extra analysis of a same-numbered item
    /// is harmless.
    /// </summary>
    internal async Task OnEncodingCompletedAsync(EncodingCompletedEvent evt, CancellationToken ct)
    {
        try
        {
            await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
            int? seasonId = await context
                .Episodes.AsNoTracking()
                .Where(e => e.Id == evt.JobId && e.VideoFiles.Any())
                .Select(e => (int?)e.SeasonId)
                .FirstOrDefaultAsync(ct);
            bool movie = await context
                .Movies.AsNoTracking()
                .AnyAsync(m => m.Id == evt.JobId && m.VideoFiles.Any(), ct);

            if (seasonId is not null)
            {
                await state.ResetFailuresAsync(SegmentAnalysisTarget.Season, seasonId.Value, ct);
                Dispatch(SegmentAnalysisJob.ForSeason(seasonId.Value));
            }

            if (movie)
            {
                await state.ResetFailuresAsync(SegmentAnalysisTarget.Movie, evt.JobId, ct);
                Dispatch(SegmentAnalysisJob.ForMovie(evt.JobId));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Segment analysis: could not queue after encode {JobId}", evt.JobId);
        }
    }

    internal async Task OnLibraryScanCompletedAsync(LibraryScanCompletedEvent evt, CancellationToken ct)
    {
        try
        {
            await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
            List<int> seasons = await context
                .Seasons.AsNoTracking()
                .Where(s =>
                    context.LibraryTv.Any(lt => lt.LibraryId == evt.LibraryId && lt.TvId == s.TvId)
                    && s.Episodes.Any(e => e.VideoFiles.Any())
                )
                .Select(s => s.Id)
                .ToListAsync(ct);
            List<int> movies = await context
                .Movies.AsNoTracking()
                .Where(m =>
                    context.LibraryMovie.Any(lm => lm.LibraryId == evt.LibraryId && lm.MovieId == m.Id)
                    && m.VideoFiles.Any()
                )
                .Select(m => m.Id)
                .ToListAsync(ct);

            HashSet<int> doneSeasons = await state.AnalysedAsync(SegmentAnalysisTarget.Season, ct);
            doneSeasons.UnionWith(await state.GivenUpAsync(SegmentAnalysisTarget.Season, ct));
            HashSet<int> doneMovies = await state.AnalysedAsync(SegmentAnalysisTarget.Movie, ct);
            doneMovies.UnionWith(await state.GivenUpAsync(SegmentAnalysisTarget.Movie, ct));
            List<int> todoSeasons = [.. seasons.Where(id => !doneSeasons.Contains(id))];
            List<int> todoMovies = [.. movies.Where(id => !doneMovies.Contains(id))];

            foreach (int seasonId in todoSeasons)
                Dispatch(SegmentAnalysisJob.ForSeason(seasonId));
            foreach (int movieId in todoMovies)
                Dispatch(SegmentAnalysisJob.ForMovie(movieId));

            if (todoSeasons.Count + todoMovies.Count > 0)
                logger.LogInformation(
                    "Segment analysis: queued {Seasons} season(s) and {Movies} movie(s) of {Library}",
                    todoSeasons.Count,
                    todoMovies.Count,
                    evt.LibraryName
                );
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Segment analysis: could not queue the backfill for {Library}", evt.LibraryName);
        }
    }

    private void Dispatch(SegmentAnalysisJob job) => dispatcher.Dispatch(job, job.QueueName, job.Priority);
}
