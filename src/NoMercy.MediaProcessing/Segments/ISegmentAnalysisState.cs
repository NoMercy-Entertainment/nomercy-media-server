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
/// Which seasons and movies the analysis already ran on, so the backfill does
/// not run it again for one where it found nothing (or where manual or chapter
/// segments blocked every analysis row).
/// </summary>
public interface ISegmentAnalysisState
{
    Task<HashSet<int>> AnalysedSeasonsAsync(CancellationToken ct);

    Task<HashSet<int>> AnalysedMoviesAsync(CancellationToken ct);

    Task MarkSeasonAsync(int seasonId, CancellationToken ct);

    Task MarkMovieAsync(int movieId, CancellationToken ct);
}
