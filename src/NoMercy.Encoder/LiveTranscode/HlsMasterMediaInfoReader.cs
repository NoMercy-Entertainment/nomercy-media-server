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

using System.Globalization;
using NoMercy.Encoder.Analysis;

namespace NoMercy.Encoder.LiveTranscode;

/// <summary>
/// Describes the variants of an HLS master playlist as <see cref="MediaInfo"/>
/// values, using only the playlist text (CODECS, RESOLUTION, FRAME-RATE,
/// VIDEO-RANGE, bandwidth). It lets the playback decision run without an ffprobe
/// pass over slow storage. The result feeds <see cref="IPlaybackDecisionEngine"/>
/// unchanged, so the codec, bit depth, range and bitrate rules live in one place.
/// </summary>
public static class HlsMasterMediaInfoReader
{
    private const string StreamInfTag = "#EXT-X-STREAM-INF:";
    private const string MediaTag = "#EXT-X-MEDIA:";

    // ffprobe reports an HLS playlist with this format name, which is what
    // PlaybackDecisionEngine maps to the "hls" container.
    private const string HlsFormat = "hls,applehttp";

    // Text-track codecs that appear in CODECS but carry no picture or sound.
    private static readonly string[] TextCodecPrefixes = ["wvtt", "stpp", "c608", "c708"];

    /// <returns>
    /// One <see cref="MediaInfo"/> per variant, or null when the text is not a master
    /// playlist, has no variants, or any variant lacks CODECS, RESOLUTION or
    /// VIDEO-RANGE, or names a codec this reader cannot classify. Null means
    /// "cannot decide from the text": the caller must probe the file.
    /// </returns>
    public static IReadOnlyList<MediaInfo>? TryRead(string? masterText, string filePath)
    {
        if (string.IsNullOrWhiteSpace(masterText))
            return null;

        string[] lines = masterText.Split('\n');
        if (lines.Length == 0 || !lines[0].Trim().StartsWith("#EXTM3U", StringComparison.Ordinal))
            return null;

        Dictionary<string, int> audioGroupChannels = ReadAudioGroupChannels(lines);
        List<MediaInfo> variants = [];

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(StreamInfTag, StringComparison.Ordinal))
                continue;

            Dictionary<string, string> attributes = ParseAttributes(line[StreamInfTag.Length..]);
            MediaInfo? variant = BuildVariant(attributes, audioGroupChannels, filePath);
            if (variant is null)
                return null;

            variants.Add(variant);
        }

        return variants.Count == 0 ? null : variants;
    }

    private static MediaInfo? BuildVariant(
        Dictionary<string, string> attributes,
        Dictionary<string, int> audioGroupChannels,
        string filePath
    )
    {
        if (
            !attributes.TryGetValue("CODECS", out string? codecs)
            || !attributes.TryGetValue("RESOLUTION", out string? resolution)
            || !attributes.TryGetValue("VIDEO-RANGE", out string? videoRange)
        )
            return null;

        if (!TryParseResolution(resolution, out int width, out int height))
            return null;

        if (!TryMapVideoRange(videoRange, out string? colorTransfer, out string? colorPrimaries))
            return null;

        double frameRate = 0;
        if (
            attributes.TryGetValue("FRAME-RATE", out string? frameRateText)
            && !double.TryParse(
                frameRateText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out frameRate
            )
        )
            return null;

        int audioChannels =
            attributes.TryGetValue("AUDIO", out string? audioGroup)
            && audioGroupChannels.TryGetValue(audioGroup, out int channels)
                ? channels
                : 2;

        VideoStreamInfo? video = null;
        AudioStreamInfo? audio = null;

        foreach (string codecEntry in codecs.Split(','))
        {
            string codec = codecEntry.Trim();
            if (TextCodecPrefixes.Any(p => codec.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (TryMapVideoCodec(codec, out string? videoCodec, out int bitDepth))
            {
                if (video is not null)
                    return null;

                video = new VideoStreamInfo(
                    Index: 0,
                    Codec: videoCodec!,
                    Width: width,
                    Height: height,
                    FrameRate: frameRate,
                    BitDepth: bitDepth,
                    PixelFormat: bitDepth > 8 ? "yuv420p10le" : "yuv420p",
                    ColorPrimaries: colorPrimaries,
                    ColorTransfer: colorTransfer,
                    ColorSpace: null,
                    IsDefault: true,
                    BitRateKbps: ReadBitrateKbps(attributes)
                );
            }
            else if (TryMapAudioCodec(codec, out string? audioCodec))
            {
                audio ??= new AudioStreamInfo(
                    Index: 1,
                    Codec: audioCodec!,
                    Channels: audioChannels,
                    SampleRate: 48000,
                    BitRateKbps: 0,
                    Language: null,
                    IsDefault: true,
                    IsForced: false
                );
            }
            else
            {
                return null;
            }
        }

        if (video is null)
            return null;

        return new MediaInfo(
            FilePath: filePath,
            Format: HlsFormat,
            Duration: TimeSpan.Zero,
            OverallBitRateKbps: video.BitRateKbps,
            FileSizeBytes: 0,
            VideoStreams: [video],
            AudioStreams: audio is null ? [] : [audio],
            SubtitleStreams: [],
            Chapters: []
        );
    }

    private static long ReadBitrateKbps(Dictionary<string, string> attributes)
    {
        string? bandwidth = attributes.GetValueOrDefault("AVERAGE-BANDWIDTH");
        if (bandwidth is null)
            bandwidth = attributes.GetValueOrDefault("BANDWIDTH");

        return long.TryParse(
            bandwidth,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long bps
        )
            ? bps / 1000
            : 0;
    }

    private static bool TryParseResolution(string text, out int width, out int height)
    {
        width = 0;
        height = 0;
        string[] parts = text.Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            && int.TryParse(
                parts[1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out height
            )
            && width > 0
            && height > 0;
    }

    // PQ and HLG carry bt2020 primaries, which is what VideoStreamInfo.IsHdr requires.
    private static bool TryMapVideoRange(
        string videoRange,
        out string? colorTransfer,
        out string? colorPrimaries
    )
    {
        colorTransfer = null;
        colorPrimaries = null;

        switch (videoRange.ToUpperInvariant())
        {
            case "SDR":
                return true;
            case "PQ":
                colorTransfer = "smpte2084";
                colorPrimaries = "bt2020";
                return true;
            case "HLG":
                colorTransfer = "arib-std-b67";
                colorPrimaries = "bt2020";
                return true;
            default:
                return false;
        }
    }

    private static bool TryMapVideoCodec(string codec, out string? name, out int bitDepth)
    {
        name = null;
        bitDepth = 8;
        string[] parts = codec.Split('.');
        string family = parts[0].ToLowerInvariant();

        switch (family)
        {
            case "avc1":
            case "avc3":
                // avc1.PPCCLL: PP is profile_idc in hex. High 10, High 4:2:2 and
                // High 4:4:4 (and CAVLC 4:4:4) are the 10-bit-capable profiles.
                if (
                    parts.Length != 2
                    || parts[1].Length != 6
                    || !int.TryParse(
                        parts[1][..2],
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out int avcProfile
                    )
                )
                    return false;
                name = "h264";
                bitDepth = avcProfile is 0x6E or 0x7A or 0xF4 or 0x2C ? 10 : 8;
                return true;
            case "hvc1":
            case "hev1":
                // hvc1.P.C.Lxxx.B0: P is general_profile_idc, optionally prefixed
                // with the profile space letter. Main and Main Still Picture are 8-bit.
                if (parts.Length < 2)
                    return false;
                if (
                    !int.TryParse(
                        parts[1].TrimStart('A', 'B', 'C', 'a', 'b', 'c'),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int hevcProfile
                    )
                )
                    return false;
                name = "hevc";
                bitDepth = hevcProfile is 1 or 3 ? 8 : 10;
                return true;
            case "av01":
            case "vp09":
                // av01.P.LLT.DD and vp09.PP.LL.DD both end the leading fields with DD.
                if (
                    parts.Length < 4
                    || !int.TryParse(
                        parts[3],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out bitDepth
                    )
                )
                    return false;
                name = family == "av01" ? "av1" : "vp9";
                return true;
            default:
                return false;
        }
    }

    private static bool TryMapAudioCodec(string codec, out string? name)
    {
        string lower = codec.ToLowerInvariant();
        name = lower switch
        {
            "ac-3" => "ac3",
            "ec-3" => "eac3",
            "opus" => "opus",
            "flac" => "flac",
            "mp3" or "mp4a.6b" or "mp4a.69" => "mp3",
            _ when lower.StartsWith("mp4a.40.", StringComparison.Ordinal) => "aac",
            _ => null,
        };
        return name is not null;
    }

    // Channel count per audio GROUP-ID, from the first rendition that states one.
    private static Dictionary<string, int> ReadAudioGroupChannels(string[] lines)
    {
        Dictionary<string, int> result = new(StringComparer.Ordinal);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(MediaTag, StringComparison.Ordinal))
                continue;

            Dictionary<string, string> attributes = ParseAttributes(line[MediaTag.Length..]);
            if (
                !string.Equals(
                    attributes.GetValueOrDefault("TYPE"),
                    "AUDIO",
                    StringComparison.Ordinal
                )
                || !attributes.TryGetValue("GROUP-ID", out string? groupId)
                || !attributes.TryGetValue("CHANNELS", out string? channelsText)
            )
                continue;

            // CHANNELS is "6" or "16/JOC": the count is the part before the slash.
            string count = channelsText.Split('/')[0];
            if (
                int.TryParse(
                    count,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int channels
                )
                && channels > 0
            )
                result[groupId] = Math.Max(channels, result.GetValueOrDefault(groupId));
        }

        return result;
    }

    // KEY=value,KEY="quoted, value" attribute list (RFC 8216 section 4.2).
    private static Dictionary<string, string> ParseAttributes(string text)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        int position = 0;

        while (position < text.Length)
        {
            int equals = text.IndexOf('=', position);
            if (equals < 0)
                break;

            string key = text[position..equals].Trim();
            position = equals + 1;

            string value;
            if (position < text.Length && text[position] == '"')
            {
                int close = text.IndexOf('"', position + 1);
                if (close < 0)
                    break;
                value = text[(position + 1)..close];
                position = close + 1;
            }
            else
            {
                int comma = text.IndexOf(',', position);
                int end = comma < 0 ? text.Length : comma;
                value = text[position..end].Trim();
                position = end;
            }

            result[key] = value;
            if (position < text.Length && text[position] == ',')
                position++;
        }

        return result;
    }
}
