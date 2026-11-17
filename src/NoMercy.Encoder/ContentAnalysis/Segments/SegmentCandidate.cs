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

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>
/// A theme candidate after stages 4–6, with all the evidence that decided it.
/// Times are rounded to 0.01 s.
/// </summary>
/// <param name="Raw">The stage-3 region, before any edge moved.</param>
/// <param name="Probe">The edges moved off absorbed silence: where the picture was probed.</param>
/// <param name="Partners">Partners in the season (episodes − 1).</param>
public sealed record SegmentCandidate(
    ThemeKind Kind,
    TimeRange Raw,
    TimeRange Probe,
    double Start,
    double End,
    double Length,
    int Support,
    int Consistent,
    int Partners,
    double Coverage,
    bool StartOnSilence,
    bool StartNearBlack,
    bool EndOnSilence,
    bool EndNearBlack,
    EdgeEvidence StartEdge,
    EdgeEvidence EndEdge,
    PictureSupport? Picture,
    bool StrongAudio,
    bool Keep
)
{
    /// <summary>True or false when the picture was compared, null when it could not be.</summary>
    public bool? PictureConfirmed => Picture?.Confirmed;
}
