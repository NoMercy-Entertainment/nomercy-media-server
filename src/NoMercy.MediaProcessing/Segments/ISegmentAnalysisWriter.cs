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
/// Stores analysis answers as <c>Source = "analysis"</c> content segments. The
/// one seam between the #598 analysis and the segment table, so the write
/// precedence of #503 applies here and nowhere else in the analysis.
/// </summary>
public interface ISegmentAnalysisWriter
{
    /// <summary>
    /// Replaces the earlier analysis rows of every item in <paramref name="items"/>
    /// (one season, or one movie) with the new answers.
    /// </summary>
    Task WriteAsync(IReadOnlyList<AnalysedItem> items, CancellationToken ct);
}
