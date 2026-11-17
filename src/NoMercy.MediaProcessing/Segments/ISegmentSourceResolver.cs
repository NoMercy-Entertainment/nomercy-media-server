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

/// <summary>Finds the encoded media of a season's episodes, or of a movie, for the analysis.</summary>
public interface ISegmentSourceResolver
{
    /// <summary>The season's encoded episodes in episode order, all in one audio language.</summary>
    Task<List<ResolvedSegmentSource>> ResolveSeasonAsync(int seasonId, CancellationToken ct);

    Task<ResolvedSegmentSource?> ResolveMovieAsync(int movieId, CancellationToken ct);
}
