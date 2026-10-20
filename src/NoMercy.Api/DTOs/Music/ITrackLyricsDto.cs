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

namespace NoMercy.Api.DTOs.Music;

/// <summary>
/// A track DTO that embeds lyrics. Filled by
/// <see cref="TrackLyricsMapping.ApplyLyrics" />, so every payload applies the
/// offset the same way and carries the same provenance.
/// </summary>
public interface ITrackLyricsDto
{
    Lyric[]? Lyrics { get; set; }
    int? LyricsOffset { get; set; }
    string? LyricsSource { get; set; }
    string? LyricsTiming { get; set; }
}
