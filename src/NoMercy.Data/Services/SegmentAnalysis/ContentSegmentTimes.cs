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

namespace NoMercy.Data.Services.SegmentAnalysis;

/// <summary>
/// The times clients act on, read from an item's segments (#598).
/// </summary>
public static class ContentSegmentTimes
{
    /// <summary>
    /// <c>credits_start</c>: where the "next episode" button may appear. The start
    /// of the item's credits (or outro) segment; with several, the latest, because
    /// a late button costs nothing and an early one hides story. Rows with
    /// negative or empty ranges (old detector output, #344) are ignored. Null when
    /// the item has no credits segment.
    /// </summary>
    public static double? CreditsStart(IEnumerable<ContentSegment> segments)
    {
        List<double> starts =
        [
            .. segments
                .Where(s =>
                    s.SegmentType is ContentSegmentType.Credits or ContentSegmentType.Outro
                    && s.StartSeconds >= 0
                    && s.EndSeconds > s.StartSeconds
                )
                .Select(s => s.StartSeconds),
        ];
        return starts.Count > 0 ? starts.Max() : null;
    }

    /// <summary>
    /// <c>content_end</c>: where auto-advance belongs, after any epilogue or
    /// preview that follows the credits. Stored on the analysis credits row; the
    /// latest when several carry one. Null when no row knows it (the client then
    /// plays to the end of the file).
    /// </summary>
    public static double? ContentEnd(IEnumerable<ContentSegment> segments)
    {
        List<double> ends =
        [
            .. segments
                .Where(s =>
                    s.SegmentType is ContentSegmentType.Credits or ContentSegmentType.Outro
                    && s.ContentEndSeconds is > 0
                )
                .Select(s => s.ContentEndSeconds!.Value),
        ];
        return ends.Count > 0 ? ends.Max() : null;
    }
}
