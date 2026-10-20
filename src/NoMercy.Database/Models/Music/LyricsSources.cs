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

namespace NoMercy.Database.Models.Music;

/// <summary>
/// Where lyric text came from. Stored in <see cref="Track.LyricsSource" /> and
/// sent to clients as-is, so a client credits the right provider. Timing is
/// recorded separately in <see cref="LyricsTimings" />: generated timing is
/// usually laid over text that still needs its own attribution.
/// </summary>
public static class LyricsSources
{
    public const string Lrclib = "lrclib";
    public const string Musixmatch = "musixmatch";
    public const string Embedded = "embedded";
    public const string Transcribed = "transcribed";
}
