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
using NoMercy.Database.Segments;

namespace NoMercy.MediaProcessing.Segments;

/// <summary>A skip range the chapter classifier found, before it is stored.</summary>
public sealed record ClassifiedChapterSegment(
    ContentSegmentType Type,
    double StartSeconds,
    double EndSeconds,
    ChapterSegmentMethod Method
)
{
    public double Confidence =>
        Method == ChapterSegmentMethod.Title
            ? ContentSegmentPrecedence.ChapterTitleConfidence
            : ContentSegmentPrecedence.ChapterLengthConfidence;
}
