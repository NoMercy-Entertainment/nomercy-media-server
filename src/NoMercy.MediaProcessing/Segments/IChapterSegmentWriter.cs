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

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// Classifies stored chapters and stores the result as
/// <c>Source = "chapters"</c> content segments under the precedence order.
/// Every call is idempotent: a rerun with unchanged chapters writes nothing.
/// </summary>
public interface IChapterSegmentWriter
{
    /// <returns>The number of items whose segments changed (0 or 1).</returns>
    Task<int> ClassifyEpisodeAsync(int episodeId, CancellationToken ct);

    /// <returns>The number of items whose segments changed (0 or 1).</returns>
    Task<int> ClassifyMovieAsync(int movieId, CancellationToken ct);

    /// <summary>
    /// The backfill: every episode and movie with stored chapters, or with
    /// chapter segments from an earlier run.
    /// </summary>
    /// <returns>The number of items whose segments changed.</returns>
    Task<int> ClassifyAllAsync(CancellationToken ct);
}
