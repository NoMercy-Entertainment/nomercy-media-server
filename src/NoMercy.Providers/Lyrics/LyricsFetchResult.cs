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

using NoMercy.Database.Models.Music;
using NoMercy.Providers.Abstractions;

namespace NoMercy.Providers.Lyrics;

/// <summary>
/// Outcome of a lyrics resolve. Distinguishes a confirmed "no lyrics anywhere"
/// (every provider was queried cleanly and none matched -- safe to permanently
/// negative-cache) from a transient failure (timeout, rate limit, unexpected
/// error on any provider stage -- must NOT be cached the same way, since the
/// track was never actually checked).
/// </summary>
/// <param name="Winner">The provider stage that answered, for the log.</param>
/// <param name="Source">Where the text came from: a <see cref="LyricsSources" /> value.</param>
/// <param name="Synced">True when the provider gave line timing, false for plain text.</param>
public sealed record LyricsFetchResult(
    LyricLine[]? Lines,
    bool IsTransientError,
    string? Winner,
    string? Source,
    bool Synced
)
{
    public static readonly LyricsFetchResult NotFound = new(null, false, null, null, false);

    public static readonly LyricsFetchResult TransientFailure = new(null, true, null, null, false);

    public static LyricsFetchResult Found(
        LyricLine[] lines,
        string winner,
        string source,
        bool synced
    ) => new(lines, false, winner, source, synced);
}
