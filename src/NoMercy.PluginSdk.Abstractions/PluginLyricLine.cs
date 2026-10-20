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

/// <summary>One lyric line. Added in ABI 12.6.</summary>
/// <param name="Text">The line as shown.</param>
/// <param name="Start">Seconds from the start of the track.</param>
/// <param name="Words">Word timings inside the line, or null when only the line is timed. The first word starts where the line starts.</param>
public sealed record PluginLyricLine(
    string Text,
    double Start,
    IReadOnlyList<PluginLyricWord>? Words
);
