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

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;

/// <summary>One episode of the research corpus: its recorded signals and picture evidence.</summary>
public sealed record RecordedEpisode(
    string Name,
    EpisodeSignals Signals,
    EdgeProbeSet? EdgeProbes,
    ThumbnailStrip? Thumbnails,
    IReadOnlyList<RecordedChapter> Chapters
);
