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

namespace NoMercy.Providers.Lyrics;

/// <summary>
/// Every validated candidate the providers returned for a track, winner or
/// not. <see cref="IsTransientError" /> is true when nothing came back and a
/// provider failed or timed out, so the caller does not cache the emptiness.
/// </summary>
public sealed record LyricsCandidatesResult(
    IReadOnlyList<LyricsTextCandidate> Candidates,
    bool IsTransientError
)
{
    public static readonly LyricsCandidatesResult TransientFailure = new([], true);
}
