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

using NoMercy.MediaProcessing.Files.AudioTracks;

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// The renditions an encoded episode's master playlist lists: video variants
/// (EXT-X-STREAM-INF) and audio renditions (read by
/// <see cref="HlsAudioRenditionReader"/>), as URIs relative to the master. The
/// analysis reads one audio rendition and one video variant directly: the audio
/// playlist alone is a few MB for a 22 min episode.
/// </summary>
public sealed record HlsMasterPlaylist(
    IReadOnlyList<string> VideoUris,
    IReadOnlyList<HlsAudioRendition> Audio
)
{
    public static HlsMasterPlaylist Parse(string text)
    {
        List<string> video = [];
        bool variantNext = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                variantNext = true;
                continue;
            }

            if (line.StartsWith('#'))
                continue;

            if (variantNext)
                video.Add(line);
            variantNext = false;
        }

        return new(video, HlsAudioRenditionReader.Read(text));
    }
}
