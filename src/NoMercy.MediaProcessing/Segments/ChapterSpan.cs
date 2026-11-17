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

namespace NoMercy.MediaProcessing.Segments;

/// <summary>One chapter of a file, in seconds from the start of the file.</summary>
public sealed record ChapterSpan(string Title, double StartSeconds, double EndSeconds)
{
    public double Length => EndSeconds - StartSeconds;

    /// <summary>
    /// A chapter as the scan stored it from chapters.vtt (milliseconds): the
    /// same list every client gets with the playlist.
    /// </summary>
    public static ChapterSpan FromStored(IChapter chapter) =>
        new(chapter.Title, chapter.StartTime / 1000.0, chapter.EndTime / 1000.0);
}
