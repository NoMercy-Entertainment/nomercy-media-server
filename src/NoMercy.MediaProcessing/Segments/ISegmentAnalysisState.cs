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
/// What the backfill remembers per season and movie: which were analysed (so a
/// season where nothing was found, or where manual and chapter segments block
/// every row, is not analysed again on every scan), and how often an analysis
/// failed (so an always-failing season stops being retried).
/// </summary>
public interface ISegmentAnalysisState
{
    /// <summary>After this many failures the backfill leaves an item alone.</summary>
    const int MaxFailures = 3;

    Task<HashSet<int>> AnalysedAsync(SegmentAnalysisTarget target, CancellationToken ct);

    /// <summary>Items that failed <see cref="MaxFailures"/> times since their last reset.</summary>
    Task<HashSet<int>> GivenUpAsync(SegmentAnalysisTarget target, CancellationToken ct);

    /// <summary>The item was analysed; its failure count is cleared.</summary>
    Task MarkAnalysedAsync(SegmentAnalysisTarget target, int id, CancellationToken ct);

    /// <summary>One more failed run. Returns the new failure count.</summary>
    Task<int> RecordFailureAsync(SegmentAnalysisTarget target, int id, CancellationToken ct);

    /// <summary>
    /// The item's files changed (a new encode) or someone asked for a run: its
    /// failures are forgotten so it is tried again.
    /// </summary>
    Task ResetFailuresAsync(SegmentAnalysisTarget target, int id, CancellationToken ct);
}
