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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.MediaProcessing.Segments;

namespace NoMercy.MediaProcessing.EventHandlers;

/// <summary>
/// Classifies an episode's or movie's chapters right after its encode. The
/// event comes after the encode's library refresh, so the chapters.vtt the
/// encoder wrote is already stored. The work is a few database reads, so it
/// runs inline; a failure is logged and never fails the encode. The daily
/// <c>ChapterSegmentSweepCronJob</c> catches anything this misses.
/// </summary>
public class ChapterSegmentSubscriber(
    IEventBus eventBus,
    IChapterSegmentWriter writer,
    ILogger<ChapterSegmentSubscriber> logger
) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(eventBus.Subscribe<EncodingCompletedEvent>(OnEncodingCompletedAsync));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (IDisposable subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    internal async Task OnEncodingCompletedAsync(EncodingCompletedEvent evt, CancellationToken ct)
    {
        try
        {
            if (evt.EpisodeId is { } episodeId)
                await writer.ClassifyEpisodeAsync(episodeId, ct);
            else if (evt.MovieId is { } movieId)
                await writer.ClassifyMovieAsync(movieId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Chapter segments could not be classified for encode {JobId}",
                evt.JobId
            );
        }
    }
}
