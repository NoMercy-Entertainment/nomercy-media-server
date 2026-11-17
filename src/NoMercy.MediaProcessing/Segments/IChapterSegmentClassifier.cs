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
    /// <param name="lengthRulesApply">
    /// True for an episode of an anime show. The length rules describe anime
    /// (a 90 s opening song, a short next-episode preview); on other shows
    /// they mistake cold opens and post-credit scenes for songs.
    /// </param>
    IReadOnlyList<ClassifiedChapterSegment> Classify(
        IReadOnlyList<ChapterSpan> chapters,
        bool lengthRulesApply
    );
}
