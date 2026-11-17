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

/// <summary>Runs the intro / credits / end-of-content analysis (#598) and stores the answers.</summary>
public interface ISegmentAnalysisService
{
    /// <summary>Analyses one season as a group. False when it had nothing to analyse.</summary>
    Task<bool> AnalyseSeasonAsync(int seasonId, CancellationToken ct);

    /// <summary>A movie gets credits and end of content only (stages 7–8).</summary>
    Task<bool> AnalyseMovieAsync(int movieId, CancellationToken ct);
}
