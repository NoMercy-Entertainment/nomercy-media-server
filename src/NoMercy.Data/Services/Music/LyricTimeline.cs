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

namespace NoMercy.Data.Services.Music;

public static class LyricTimeline
{
    /// <summary>A line time from seconds, with the minutes, seconds and hundredths the clients read.</summary>
    public static Lyric.LineTime FromSeconds(double seconds)
    {
        int totalHundredths = (int)Math.Round(seconds * 100);
        return new()
        {
            Total = seconds,
            Minutes = totalHundredths / 6000,
            Seconds = totalHundredths / 100 % 60,
            Hundredths = totalHundredths % 100,
        };
    }

    /// <summary>
    /// Shifts every line by the track's offset. A line pushed before the start of
    /// the track is clamped to zero.
    /// </summary>
    public static Lyric[] ShiftBy(Lyric[] lyrics, int? offsetMs)
    {
        if (offsetMs is null or 0)
            return lyrics;

        double offsetSec = offsetMs.Value / 1000.0;
        return
        [
            .. lyrics.Select(line =>
            {
                return new Lyric
                {
                    Text = line.Text,
                    Time = FromSeconds(Math.Max(0, line.Time.Total + offsetSec)),
                    Words = line
                        .Words?.Select(word => new LyricWord
                        {
                            Text = word.Text,
                            Start = Math.Max(0, word.Start + offsetSec),
                            End = Math.Max(0, word.End + offsetSec),
                        })
                        .ToArray(),
                };
            }),
        ];
    }
}
