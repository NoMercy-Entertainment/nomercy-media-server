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

/// <summary>One lyric text the host found, text only. Added in ABI 12.6.</summary>
/// <param name="Source">Where it came from: <c>lrclib</c>, <c>musixmatch</c> or <c>embedded</c>.</param>
/// <param name="Lines">One entry per lyric line, in order, without timing.</param>
/// <param name="Language">ISO 639-1 when the source says so; null otherwise.</param>
/// <param name="WasSynced">True when the source carried line timing, which the host did not pass on here.</param>
public sealed record PluginTextCandidate(
    string Source,
    IReadOnlyList<string> Lines,
    string? Language,
    bool WasSynced
);
