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

/// <summary>Decides which chapters of a file are an opening, a recap or an ending.</summary>
public interface IChapterSegmentClassifier
{
    /// <param name="chapters">The file's chapters in playback order.</param>
    /// <param name="isAnime">
    /// True for an episode of an anime show. Anime turns on the length rules
    /// (a 90 s opening song, a short next-episode preview), which on other
    /// shows mistake cold opens and post-credit scenes for songs, and makes
    /// "Intro" an opening only at an opening song's length: anime releases use
    /// "Intro" for the cold open, western releases for the theme.
    /// </param>
    IReadOnlyList<ClassifiedChapterSegment> Classify(
        IReadOnlyList<ChapterSpan> chapters,
        bool isAnime
    );
}
