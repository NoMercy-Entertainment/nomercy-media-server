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

using NoMercy.Database.Models.Media;
using NoMercy.Database.Segments;

namespace NoMercy.MediaProcessing.Segments;

/// <summary>Reads stored chapters and writes the segments classified from them.</summary>
public interface IChapterSegmentRepository
{
    /// <summary>
    /// The chapters of the given episodes. Every requested episode gets a
    /// source, with an empty list when it has no chapters or no video file any
    /// more, so its old chapter rows are removed.
    /// </summary>
    Task<List<ChapterSource>> GetEpisodeSourcesAsync(
        IReadOnlyList<int> episodeIds,
        CancellationToken ct
    );

    /// <summary>The chapters of the given movies, as <see cref="GetEpisodeSourcesAsync"/>.</summary>
    Task<List<ChapterSource>> GetMovieSourcesAsync(
        IReadOnlyList<int> movieIds,
        CancellationToken ct
    );

    /// <summary>
    /// Every episode id with stored chapters or a stored chapter segment, in
    /// ascending order: what a backfill has to visit.
    /// </summary>
    Task<List<int>> GetEpisodeIdsToClassifyAsync(CancellationToken ct);

    /// <summary>Every movie id to visit, as <see cref="GetEpisodeIdsToClassifyAsync"/>.</summary>
    Task<List<int>> GetMovieIdsToClassifyAsync(CancellationToken ct);

    /// <summary>Every stored segment, of any source, of the given episodes and movies.</summary>
    Task<List<ContentSegment>> GetSegmentsAsync(
        IReadOnlyList<int> episodeIds,
        IReadOnlyList<int> movieIds,
        CancellationToken ct
    );

    /// <summary>Applies the plans in one save.</summary>
    Task ApplyAsync(IReadOnlyList<ContentSegmentWritePlan> plans, CancellationToken ct);
}
