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

namespace NoMercy.MediaProcessing.Files.AudioTracks;

/// <summary>One <c>EXT-X-MEDIA:TYPE=AUDIO</c> entry of an HLS master playlist.</summary>
public sealed record HlsAudioRendition(string GroupId, string Uri, string Language, bool IsDefault);

public static class HlsAudioRenditionReader
{
    private const string MediaTag = "#EXT-X-MEDIA:";

    public static IReadOnlyList<HlsAudioRendition> Read(string masterText)
    {
        List<HlsAudioRendition> renditions = [];

        foreach (string rawLine in masterText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(MediaTag, StringComparison.Ordinal))
                continue;

            Dictionary<string, string> attributes = ParseAttributes(line[MediaTag.Length..]);
            if (
                attributes.GetValueOrDefault("TYPE") != "AUDIO"
                || !attributes.TryGetValue("URI", out string? uri)
                || uri.Length == 0
            )
                continue;

            renditions.Add(
                new(
                    GroupId: attributes.GetValueOrDefault("GROUP-ID", string.Empty),
                    Uri: uri,
                    Language: attributes.GetValueOrDefault("LANGUAGE", "und"),
                    IsDefault: attributes.GetValueOrDefault("DEFAULT") == "YES"
                )
            );
        }

        return renditions;
    }

    // Attribute values may be quoted and may contain commas, so split by hand.
    private static Dictionary<string, string> ParseAttributes(string text)
    {
        Dictionary<string, string> attributes = new(StringComparer.Ordinal);
        int position = 0;

        while (position < text.Length)
        {
            int equals = text.IndexOf('=', position);
            if (equals < 0)
                break;

            string key = text[position..equals].Trim();
            int valueStart = equals + 1;
            string value;

            if (valueStart < text.Length && text[valueStart] == '"')
            {
                int close = text.IndexOf('"', valueStart + 1);
                if (close < 0)
                    close = text.Length;
                value = text[(valueStart + 1)..close];
                position = Math.Min(close + 2, text.Length);
            }
            else
            {
                int comma = text.IndexOf(',', valueStart);
                if (comma < 0)
                    comma = text.Length;
                value = text[valueStart..comma].Trim();
                position = comma + 1;
            }

            attributes[key] = value;
        }

        return attributes;
    }
}
