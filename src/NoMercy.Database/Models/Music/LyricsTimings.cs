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
/// Where the line timing of a track's lyrics came from. Stored in
/// <see cref="Track.LyricsTiming" />. <see cref="Provider" /> is a synced result
/// from the provider named in <see cref="Track.LyricsSource" />,
/// <see cref="None" /> is plain text with no timing, and
/// <see cref="Generated" /> is timing produced on this server over provider text.
/// </summary>
public static class LyricsTimings
{
    public const string Provider = "provider";
    public const string Generated = "generated";
    public const string None = "none";
}
