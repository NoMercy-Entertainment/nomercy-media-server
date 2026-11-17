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
/// The chapters of one episode or movie, as the scan stored them from its
/// chapters.vtt. Exactly one of <see cref="EpisodeId"/> and
/// <see cref="MovieId"/> is set. An empty <see cref="Chapters"/> list means the
/// item has no chapters (any more).
/// </summary>
public sealed record ChapterSource(
    int? EpisodeId,
    int? MovieId,
    bool IsAnime,
    IReadOnlyList<ChapterSpan> Chapters
);
