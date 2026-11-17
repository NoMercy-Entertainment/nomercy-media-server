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

namespace NoMercy.Events.Encoding;

public sealed class EncodingCompletedEvent : EventBase
{
    public override string Source => "Encoder";

    public required int JobId { get; init; }
    public required string OutputPath { get; init; }
    public required TimeSpan Duration { get; init; }

    // Movie backdrop or episode still, and movie/show poster, as already
    // resolved for this encode. Null when the job never reached that lookup
    // (e.g. a pre-metadata failure) or the title carries no artwork.
    public string? BackdropPath { get; init; }
    public string? PosterPath { get; init; }

    // The episode or movie a video encode produced; null when the publisher
    // does not know it (a music encode). JobId alone cannot tell the two apart:
    // an episode and a movie can share an id.
    public int? EpisodeId { get; init; }
    public int? MovieId { get; init; }
}
