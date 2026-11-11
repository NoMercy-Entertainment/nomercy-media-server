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

namespace NoMercy.Encoder.Output;

/// <summary>
/// Repairs an HLS master whose video variants list only an audio codec. A
/// copied video stream has no known codec string, and older builds then wrote
/// the audio codec alone, which players read as a stream without video.
/// </summary>
public static partial class HlsMasterAudioOnlyCodecsRepair
{
    private const string StreamInfTag = "#EXT-X-STREAM-INF:";

    private static readonly string[] VideoCodecPrefixes =
    [
        "avc1",
        "avc3",
        "hvc1",
        "hev1",
        "dvh1",
        "dvhe",
        "av01",
        "vp09",
        "vp8",
    ];

    [GeneratedRegex(
        ",CODECS=\"(?<value>[^\"]*)\"",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex CodecsAttribute();

    /// <summary>
    /// Returns <paramref name="master"/> without the audio-only <c>CODECS</c>
    /// attribute on each video variant, or the same instance when nothing
    /// needs repair, so a caller can skip the write.
    /// </summary>
    public static string DropAudioOnlyCodecs(string master)
    {
        string[] lines = master.Split('\n');
        bool changed = false;

        for (int i = 0; i < lines.Length; i++)
        {
            if (
                !lines[i].StartsWith(StreamInfTag, StringComparison.Ordinal)
                || !lines[i].Contains("RESOLUTION=", StringComparison.Ordinal)
            )
                continue;

            Match codecs = CodecsAttribute().Match(lines[i]);
            if (!codecs.Success || ListsVideoCodec(codecs.Groups["value"].Value))
                continue;

            lines[i] = lines[i].Remove(codecs.Index, codecs.Length);
            changed = true;
        }

        return changed ? string.Join('\n', lines) : master;
    }

    private static bool ListsVideoCodec(string codecs) =>
        codecs
            .Split(',', StringSplitOptions.TrimEntries)
            .Any(codec =>
                VideoCodecPrefixes.Any(prefix =>
                    codec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                )
            );
}
