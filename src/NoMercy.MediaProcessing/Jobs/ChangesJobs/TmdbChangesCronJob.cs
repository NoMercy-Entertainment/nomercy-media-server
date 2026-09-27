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
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Movies;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Providers.TMDB.Client;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Jobs.ChangesJobs;

/// <summary>
/// Keeps locally held metadata in sync with TMDB. Every day it pulls the global change
/// lists for movies, shows and people, intersects them with the items in the library and
/// re-imports only what actually changed — no manual refresh, no full-catalog re-fetch.
/// </summary>
public class TmdbChangesCronJob : ICronJobExecutor
{
    private readonly ILogger<TmdbChangesCronJob> _logger;
    private readonly MediaContext _context;

    private const int LookbackDays = 2;
    private const int MaxChangePages = 500;
    private const int TmdbExportHourUtc = 8;
    private const int PostExportBufferMinutes = 10;

    // 08:10 UTC — a few minutes after TMDB publishes its daily data export. Cron schedules
    // are evaluated in UTC (see CronWorker), so this lands on TMDB's window on every host.
    public string CronExpression =>
        new CronExpressionBuilder().Daily(TmdbExportHourUtc, PostExportBufferMinutes);

    public string JobName => "TMDB Daily Changes Sync";

    public TmdbChangesCronJob(ILogger<TmdbChangesCronJob> logger, MediaContext context)
    {
        _logger = logger;
        _context = context;
    }

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        string startDate = DateTime.UtcNow.AddDays(-LookbackDays).ToString("yyyy-MM-dd");
        string endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        using TmdbChangesClient changesClient = new();
        JobDispatcher jobDispatcher = new();

        int movies = await SyncMovies(
            changesClient,
            jobDispatcher,
            startDate,
            endDate,
            cancellationToken
        );
        int shows = await SyncShows(
            changesClient,
            jobDispatcher,
            startDate,
            endDate,
            cancellationToken
        );
        int people = await SyncPeople(
            changesClient,
            jobDispatcher,
            startDate,
            endDate,
            cancellationToken
        );

        _logger.LogInformation(
            "TMDB changes sync queued refreshes — movies: {Movies}, shows: {Shows}, people: {People}", [movies, shows, people]
        );
    }

    private async Task<int> SyncMovies(
        TmdbChangesClient changesClient,
        JobDispatcher jobDispatcher,
        string startDate,
        string endDate,
        CancellationToken cancellationToken
    )
    {
        HashSet<int> changedIds = (
            await changesClient.MovieChanges(startDate, endDate, MaxChangePages) ?? []
        )
            .Select(change => change.Id)
            .ToHashSet();

        if (changedIds.Count == 0)
            return 0;

        List<LibraryMovie> matches = (await _context.LibraryMovie.ToListAsync(cancellationToken))
            .Where(link => changedIds.Contains(link.MovieId))
            .ToList();

        foreach (LibraryMovie link in matches)
            jobDispatcher.DispatchJob<MovieImportJob>(link.MovieId, link.LibraryId);

        return matches.Count;
    }

    private async Task<int> SyncShows(
        TmdbChangesClient changesClient,
        JobDispatcher jobDispatcher,
        string startDate,
        string endDate,
        CancellationToken cancellationToken
    )
    {
        HashSet<int> changedIds = (
            await changesClient.TvChanges(startDate, endDate, MaxChangePages) ?? []
        )
            .Select(change => change.Id)
            .ToHashSet();

        if (changedIds.Count == 0)
            return 0;

        // Dispatch once per SHOW, using Tvs.LibraryId - the show's real,
        // current library - never once per LibraryTv row. A stale LibraryTv
        // link left behind by an earlier re-file must not steer a re-import
        // back into the wrong library. A show with no library yet is
        // skipped: there is nothing to re-import it into.
        List<ShowLibraryRef> shows = await _context
            .Tvs.Where(tv => changedIds.Contains(tv.Id))
            .Select(tv => new ShowLibraryRef(tv.Id, tv.LibraryId))
            .ToListAsync(cancellationToken);

        List<ShowLibraryRef> matches = shows
            .Where(show => show.LibraryId != Ulid.Empty)
            .ToList();

        foreach (ShowLibraryRef match in matches)
            jobDispatcher.DispatchJob<ShowImportJob>(match.Id, match.LibraryId);

        return matches.Count;
    }

    /// <summary>A changed show and the library it is actually filed under.</summary>
    private sealed record ShowLibraryRef(int Id, Ulid LibraryId);

    private async Task<int> SyncPeople(
        TmdbChangesClient changesClient,
        JobDispatcher jobDispatcher,
        string startDate,
        string endDate,
        CancellationToken cancellationToken
    )
    {
        HashSet<int> changedIds = (
            await changesClient.PersonChanges(startDate, endDate, MaxChangePages) ?? []
        )
            .Select(change => change.Id)
            .ToHashSet();

        if (changedIds.Count == 0)
            return 0;

        List<int> matches = (
            await _context.People.Select(person => person.Id).ToListAsync(cancellationToken)
        )
            .Where(changedIds.Contains)
            .ToList();

        foreach (int personId in matches)
            jobDispatcher.DispatchJob<PersonRefreshJob>(personId, Ulid.Empty);

        return matches.Count;
    }
}
