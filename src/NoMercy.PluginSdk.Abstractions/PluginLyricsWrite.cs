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
/// One write of generated lyrics. Added in ABI 12.6.
/// </summary>
/// <param name="Lines">The lines in order of <see cref="PluginLyricLine.Start" />, never empty.</param>
/// <param name="Source">Where the text came from: <c>lrclib</c>, <c>musixmatch</c>, <c>embedded</c>, or <c>transcribed</c> when there was none.</param>
/// <param name="ExpectedVersion">The <see cref="PluginLyricsState.Version" /> the plugin read before aligning.</param>
public sealed record PluginLyricsWrite(
    IReadOnlyList<PluginLyricLine> Lines,
    string Source,
    long ExpectedVersion
);
