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
using NoMercy.Database;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Service.Jobs;

/// <inheritdoc />
public class AudioAnalysisScheduler : IAudioAnalysisScheduler
{
    /// <summary>
    /// Read per page. A large library must not be materialized into memory in
    /// one pass; the tracks are handed to the queue a page at a time.
    /// </summary>
    private const int BatchSize = 500;

    private readonly IJobDispatcher _dispatcher;
    private readonly IAudioAnalyzer _analyzer;
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly ILogger<AudioAnalysisScheduler> _logger;

    public AudioAnalysisScheduler(
        IJobDispatcher dispatcher,
        IAudioAnalyzer analyzer,
        IDbContextFactory<MediaContext> contextFactory,
        ILogger<AudioAnalysisScheduler> logger
    )
    {
        _dispatcher = dispatcher;
        _analyzer = analyzer;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<int> QueueAsync(
        IReadOnlyCollection<Ulid> libraryIds,
        CancellationToken cancellationToken = default
    )
    {
        if (libraryIds.Count == 0)
        {
            return 0;
        }

        await using MediaContext mediaContext = await _contextFactory.CreateDbContextAsync(
            cancellationToken
        );

        int version = _analyzer.Version;
        int queued = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip past what this run already queued rather than re-reading the
            // first page: dispatching writes no verdict, so the same page would
            // come back for ever and the rest of the library would never be
            // reached. The queue's payload dedup absorbs the overlap when a
            // worker finishes a track mid-run.
            List<Guid> trackIds = await AudioAnalysisQueries
                .TracksNeedingAnalysis(mediaContext, libraryIds, version)
                .OrderBy(trackId => trackId)
                .Skip(queued)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (trackIds.Count == 0)
            {
                break;
            }

            foreach (Guid trackId in trackIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _dispatcher.Dispatch(new MusicAnalysisJob { TrackId = trackId });
            }

            queued += trackIds.Count;

            if (trackIds.Count < BatchSize)
            {
                break;
            }
        }

        if (queued > 0)
        {
            _logger.LogInformation(
                "Audio analysis queued {Queued} track(s) across {Libraries} library(ies)",
                [queued, libraryIds.Count]
            );
        }

        return queued;
    }
}
