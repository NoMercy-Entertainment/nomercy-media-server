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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// The lyrics of a track as the host holds them. Line times are the stored
/// times; the offset is reported, not applied. Added in ABI 12.6.
/// </summary>
/// <param name="Lines">Null when the track was never checked, empty when it was checked and nothing was found.</param>
/// <param name="Source">Where the text came from: <c>lrclib</c>, <c>musixmatch</c>, <c>embedded</c>, <c>transcribed</c>, or null on a row from before provenance was recorded.</param>
/// <param name="Timing">Where the timing came from: <c>provider</c>, <c>generated</c>, <c>none</c>, or null on a row from before provenance was recorded.</param>
/// <param name="HasSyncedTiming">True when any line has a time above zero. Covers a legacy row whose <paramref name="Timing" /> is null.</param>
/// <param name="OffsetMs">The owner's offset, in milliseconds, or null when none is set.</param>
/// <param name="Version">Bumped by every lyrics write. A write names the version it read.</param>
public sealed record PluginLyricsState(
    IReadOnlyList<PluginLyricLine>? Lines,
    string? Source,
    string? Timing,
    bool HasSyncedTiming,
    int? OffsetMs,
    long Version
);
