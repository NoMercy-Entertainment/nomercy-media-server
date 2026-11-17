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

using System.Text.RegularExpressions;

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// The renditions an encoded episode's master playlist lists: video variants
/// (EXT-X-STREAM-INF) and audio renditions (EXT-X-MEDIA TYPE=AUDIO), as URIs
/// relative to the master. The analysis reads one audio rendition and one video
/// variant directly: the audio playlist alone is a few MB for a 22 min episode.
/// </summary>
public sealed partial record HlsMasterPlaylist(
    IReadOnlyList<string> VideoUris,
    IReadOnlyList<HlsAudioRendition> Audio
)
{
    public static HlsMasterPlaylist Parse(string text)
    {
        List<string> video = [];
        List<HlsAudioRendition> audio = [];
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

            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) && line.Contains("TYPE=AUDIO", StringComparison.Ordinal))
            {
                string? uri = Attribute(line, "URI");
                if (uri is not null)
                    audio.Add(new(Attribute(line, "LANGUAGE"), uri, Attribute(line, "DEFAULT") == "YES"));
                continue;
            }

            if (line.StartsWith('#'))
                continue;

            if (variantNext)
                video.Add(line);
            variantNext = false;
        }

        return new(video, audio);
    }

    private static string? Attribute(string line, string name)
    {
        foreach (Match match in AttributeRegex().Matches(line))
        {
            if (match.Groups["name"].Value == name)
                return match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value;
        }

        return null;
    }

    [GeneratedRegex("""(?<name>[A-Z0-9-]+)=(?:"(?<quoted>[^"]*)"|(?<bare>[^,]*))""")]
    private static partial Regex AttributeRegex();
}
