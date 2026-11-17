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

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// Which audio stream of a single (non-HLS) file the analysis reads, from what
/// the database knows about the file's tracks: the stream in the season's
/// language, else the default stream, else the first.
/// </summary>
/// <param name="Language">The file's own code of the chosen language, for FFmpeg's language specifier; null when none matched.</param>
/// <param name="Index">The audio-relative stream index (<c>0:a:N</c>) to fall back to.</param>
public sealed record SingleFileAudio(string? Language, int Index)
{
    /// <param name="tracks">The file's audio tracks in stream order.</param>
    /// <param name="seasonLanguage">The language the season is analysed in (see <see cref="AudioLanguagePicker"/>).</param>
    public static SingleFileAudio Choose(
        IReadOnlyList<(string Language, bool IsDefault)> tracks,
        string? seasonLanguage
    )
    {
        int match = -1;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (AudioLanguagePicker.Same(tracks[i].Language, seasonLanguage))
            {
                match = i;
                break;
            }
        }

        if (match >= 0)
            return new(tracks[match].Language, match);

        int fallback = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].IsDefault)
            {
                fallback = i;
                break;
            }
        }

        return new(null, fallback);
    }
}
