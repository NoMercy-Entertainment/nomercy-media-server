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

namespace NoMercy.MediaProcessing.Lyrics;

/// <summary>
/// Lyrics read from a music file's own tags: ID3 USLT or SYLT, Vorbis
/// LYRICS or UNSYNCEDLYRICS, MP4 lyrics.
/// </summary>
/// <param name="Lines">One entry per line, trimmed, without timing.</param>
/// <param name="WasSynced">True when the tag carried line timing: a SYLT frame, or LRC timestamps in the text.</param>
/// <param name="Language">ISO 639-1 when the tag says so; null otherwise.</param>
public sealed record EmbeddedLyrics(IReadOnlyList<string> Lines, bool WasSynced, string? Language);
