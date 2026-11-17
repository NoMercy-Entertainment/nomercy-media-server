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

using NoMercy.Encoder.ContentAnalysis.Segments;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>Analysis answers shaped like Hellsing S01E01's, for the persistence tests.</summary>
internal static class SegmentAnswers
{
    public static SegmentCandidate Intro(bool pictureConfirmed = true) =>
        new(
            ThemeKind.Intro,
            new(0, 82.5),
            new(3.02, 81.07),
            5.56,
            82.72,
            77.16,
            12,
            12,
            12,
            1.0,
            true,
            true,
            true,
            false,
            EdgeEvidence.Black,
            EdgeEvidence.Black,
            pictureConfirmed ? new PictureSupport(12, 12) : null,
            true,
            true
        );

    public static OutroBlock Credits(
        OutroSources sources = OutroSources.Credits | OutroSources.Audio,
        bool dark = true,
        bool text = true
    ) => new(1281.64, 1361.08, 79.44, sources, dark, text);

    public static EpisodeSegments Episode(
        string key = "1",
        SegmentCandidate? intro = null,
        OutroBlock? outro = null,
        bool withIntro = true
    )
    {
        OutroBlock credits = outro ?? Credits();
        return new(
            key,
            1380.21,
            withIntro ? intro ?? Intro() : null,
            [credits],
            new TimeRange(credits.End, 1377.27),
            credits.Start,
            new(1377.27, ContentEndEvidence.Silence),
            [],
            []
        );
    }
}
