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
using NoMercy.Database.Models.Media;
using NoMercy.Database.Segments;
using NoMercy.MediaProcessing.Segments;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>
/// A real repository whose save fails, as when another writer deleted a row
/// the plan removes, whenever a plan touches one episode.
/// </summary>
internal sealed class ConflictingChapterSegmentRepository(
    IChapterSegmentRepository inner,
    int failingEpisodeId,
    bool failOnce = false
) : IChapterSegmentRepository
{
    public int Failures { get; private set; }

    public Task<List<ChapterSource>> GetEpisodeSourcesAsync(
        IReadOnlyList<int> episodeIds,
        CancellationToken ct
    ) => inner.GetEpisodeSourcesAsync(episodeIds, ct);

    public Task<List<ChapterSource>> GetMovieSourcesAsync(
        IReadOnlyList<int> movieIds,
        CancellationToken ct
    ) => inner.GetMovieSourcesAsync(movieIds, ct);

    public Task<List<int>> GetEpisodeIdsToClassifyAsync(CancellationToken ct) =>
        inner.GetEpisodeIdsToClassifyAsync(ct);

    public Task<List<int>> GetMovieIdsToClassifyAsync(CancellationToken ct) =>
        inner.GetMovieIdsToClassifyAsync(ct);

    public Task<List<ContentSegment>> GetSegmentsAsync(
        IReadOnlyList<int> episodeIds,
        IReadOnlyList<int> movieIds,
        CancellationToken ct
    ) => inner.GetSegmentsAsync(episodeIds, movieIds, ct);

    public Task ApplyAsync(IReadOnlyList<ContentSegmentWritePlan> plans, CancellationToken ct)
    {
        bool touchesFailing = plans.Any(plan =>
            plan.Add.Concat(plan.Remove).Any(row => row.EpisodeId == failingEpisodeId)
        );
        if (touchesFailing && !(failOnce && Failures > 0))
        {
            Failures++;
            throw new DbUpdateConcurrencyException("another writer deleted a planned row");
        }

        return inner.ApplyAsync(plans, ct);
    }
}
