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

using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Segments;
using NoMercy.MediaProcessing.Segments;

namespace NoMercy.Data.Services.SegmentAnalysis;

/// <summary>
/// Stores analysis answers under the one precedence order of #503: per item,
/// <see cref="ContentSegmentPrecedence.Plan"/> decides what the analysis may
/// write, and the shared apply step (<see cref="IContentSegmentRepository.ApplyAsync"/>)
/// stores it, re-checking manual rows inside its transaction. Analysis
/// outranks chapters chosen by length, ties with the older detector (the
/// newest automatic result wins), and loses to manual rows, chapter titles
/// and imports.
/// </summary>
public sealed class ContentSegmentAnalysisWriter(
    IContentSegmentRepository repository,
    ISegmentAnalysisLocks locks
) : ISegmentAnalysisWriter
{
    /// <summary>One key for every analysis write: they are short, and a plan made from a snapshot another write is changing would add a second row.</summary>
    private const string WriteLock = "segment-analysis-write";

    public async Task WriteAsync(IReadOnlyList<AnalysedItem> items, CancellationToken ct)
    {
        await using IAsyncDisposable held = await locks.AcquireAsync(WriteLock, ct);

        List<int> episodeIds =
        [
            .. items.Where(i => i.EpisodeId is not null).Select(i => i.EpisodeId!.Value),
        ];
        List<ContentSegment> existing =
            episodeIds.Count > 0 ? await repository.GetForEpisodesAsync(episodeIds) : [];
        foreach (
            int movieId in items.Where(i => i.MovieId is not null).Select(i => i.MovieId!.Value)
        )
        {
            ct.ThrowIfCancellationRequested();
            existing.AddRange(await repository.GetForMovieAsync(movieId));
        }

        List<ContentSegmentWritePlan> plans = [];
        foreach (AnalysedItem item in items)
        {
            ct.ThrowIfCancellationRequested();
            ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
                [
                    .. existing.Where(s =>
                        s.EpisodeId == item.EpisodeId && s.MovieId == item.MovieId
                    ),
                ],
                AnalysisSegmentRows.For(item.EpisodeId, item.MovieId, item.Answer),
                ContentSegmentSource.Analysis
            );
            if (!plan.IsEmpty)
                plans.Add(plan);
        }

        await repository.ApplyAsync(plans);
    }
}
