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

using NoMercy.Data.Services.Music;
using NoMercy.Database.Models.Music;

namespace NoMercy.Api.DTOs.Music;

public static class TrackLyricsMapping
{
    /// <summary>
    /// Every lyric time the server sends is final. The track offset is applied
    /// here, once, so the embedded lyrics and <c>GET /lyrics</c> agree on the
    /// timeline. <c>lyrics_offset</c> is informational, for the adjustment UI;
    /// a client never shifts the times again. The negative marker <c>[]</c> and
    /// a null (never fetched) pass through unchanged.
    /// </summary>
    public static void ApplyLyrics(this ITrackLyricsDto dto, Track track)
    {
        dto.Lyrics = track.Lyrics is null
            ? null
            : LyricTimeline.ShiftBy(track.Lyrics, track.LyricsOffset);
        dto.LyricsOffset = track.LyricsOffset;
        dto.LyricsSource = track.LyricsSource;
        dto.LyricsTiming = track.LyricsTiming;
    }
}
