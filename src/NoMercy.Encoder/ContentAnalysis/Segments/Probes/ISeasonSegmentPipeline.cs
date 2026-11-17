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

namespace NoMercy.Encoder.ContentAnalysis.Segments.Probes;

/// <summary>
/// The whole #598 analysis for one season (or one movie): FFmpeg passes,
/// matching, picture evidence and the per-episode answer.
/// </summary>
public interface ISeasonSegmentPipeline
{
    /// <summary>
    /// Answers for the episodes whose audio could be read, in input order. An
    /// episode whose audio pass failed is left out (and, with fewer than three
    /// left, the season gets credits and content end only).
    /// </summary>
    Task<IReadOnlyList<EpisodeSegments>> AnalyseAsync(
        IReadOnlyList<SegmentAnalysisInput> episodes,
        CancellationToken ct
    );
}
