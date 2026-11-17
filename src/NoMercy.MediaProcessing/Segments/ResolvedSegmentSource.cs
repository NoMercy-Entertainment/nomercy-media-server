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

using NoMercy.Encoder.ContentAnalysis.Segments.Probes;

namespace NoMercy.MediaProcessing.Segments;

/// <summary>An episode or movie (exactly one id is set) and where its media is read from.</summary>
public sealed record ResolvedSegmentSource(int? EpisodeId, int? MovieId, SegmentMediaSource Source);
