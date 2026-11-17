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

/// <summary>Stage 6: which candidates are kept.</summary>
public static class CandidateRules
{
    /// <summary>
    /// Audio evidence strong enough without a picture match, for openings whose
    /// clips vary per episode (Rick and Morty): most partners at the same place,
    /// covering nearly all of it, long enough, and early. Squid Game's reused
    /// score reached 0.8 / 0.94 / 19 s; a 26 s one sat at 33 % of the episode.
    /// </summary>
    public static bool StrongAudio(ThemeRegion region, double length, int partners) =>
        partners > 0
        && (double)region.Consistent / partners >= SegmentRules.StrongSameFraction
        && region.Coverage >= SegmentRules.StrongCoverage
        && length >= SegmentRules.StrongMinSeconds
        && region.Position <= SegmentRules.StrongMaxPosition;

    /// <summary>
    /// At least 15 s, with hard edges at both ends or at one end plus a picture
    /// match (Yakuza E04: no cut at the end of its title sequence). An intro also
    /// needs a picture match or strong audio: reused score fails both. A picture
    /// that could not be compared counts as not confirmed, and so does an edge
    /// that was never probed.
    /// </summary>
    public static bool Keep(
        ThemeKind kind,
        double length,
        EdgeEvidence start,
        EdgeEvidence end,
        bool? pictureConfirmed,
        bool strongAudio
    )
    {
        if (length < SegmentRules.MinSegmentSeconds)
            return false;

        bool hardStart = IsHard(start);
        bool hardEnd = IsHard(end);
        bool both = hardStart && hardEnd;
        bool one = hardStart || hardEnd;
        if (!(both || (pictureConfirmed == true && one)))
            return false;

        return kind != ThemeKind.Intro || pictureConfirmed == true || strongAudio;
    }

    private static bool IsHard(EdgeEvidence evidence) =>
        evidence is not (EdgeEvidence.None or EdgeEvidence.Unprobed);
}
