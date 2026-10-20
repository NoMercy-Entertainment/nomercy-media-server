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
using Newtonsoft.Json.Linq;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Storage;

namespace NoMercy.Encoder.Analysis;

public class MediaAnalyzer(IProcessRunner processRunner, IStorage storage, EncoderOptions options)
    : IMediaAnalyzer
{
    private static readonly string[] FfprobeArgs =
    [
        "-v",
        "quiet",
        "-print_format",
        "json",
        "-show_format",
        "-show_streams",
        "-show_chapters",
    ];

    public async Task<MediaInfo> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
        await AnalyzeAsync(filePath, storage, ct);

    public async Task<MediaInfo> AnalyzeAsync(
        string filePath,
        string[]? extraInputArgs,
        CancellationToken ct = default
    )
    {
        bool isProtocolInput =
            filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || filePath.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase)
            // extraInputArgs (e.g. "-f dvdvideo"/"-f libcdio") is the caller
            // telling us this is a disc/device path, not a real file — CD/DVD
            // input paths carry no distinguishing URL scheme of their own.
            || extraInputArgs is { Length: > 0 };

        // Filesystem source: keep the scope-validated, remote-staging path.
        // Extra input args are only meaningful for protocol inputs (HTTP
        // self-ingest URL, or an optical disc protocol) — a real path has no
        // ffmpeg "-playlist"/"-title" style selector to carry.
        if (!isProtocolInput)
            return await AnalyzeAsync(filePath, storage, ct);

        string[] arguments = [.. FfprobeArgs, .. (extraInputArgs ?? []), filePath];
        ProcessResult result = await processRunner.RunAsync(
            options.FfprobePath,
            arguments,
            null,
            ct
        );

        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"ffprobe failed for '{filePath}': {result.StdErr}"
            );

        return ParseFfprobeJson(result.StdOut, filePath);
    }

    public async Task<MediaInfo> AnalyzeAsync(
        string filePath,
        IStorage sourceStorage,
        CancellationToken ct = default
    )
    {
        await using LocalPathLease inputLease = sourceStorage.AcquireLocalPath(filePath);
        string[] arguments = [.. FfprobeArgs, inputLease.Path];
        ProcessResult result = await processRunner.RunAsync(
            options.FfprobePath,
            arguments,
            null,
            ct
        );

        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"ffprobe failed for '{filePath}': {result.StdErr}"
            );

        return ParseFfprobeJson(result.StdOut, filePath);
    }

    internal static MediaInfo ParseFfprobeJson(string json, string filePath)
    {
        // ffprobe occasionally produces partial output on a damaged container
        // (truncated mkv tail, EAGAIN mid-write). Surface that as an
        // InvalidOperationException with the file path attached so the
        // caller can log meaningfully instead of dropping a raw
        // JsonReaderException with no context.
        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            throw new InvalidOperationException(
                $"ffprobe output for '{filePath}' was not valid JSON: {ex.Message}",
                ex
            );
        }
        JArray streams = root["streams"] as JArray ?? [];
        JArray chapters = root["chapters"] as JArray ?? [];
        JObject format = root["format"] as JObject ?? new JObject();

        List<VideoStreamInfo> videoStreams = [];
        List<AudioStreamInfo> audioStreams = [];
        List<SubtitleStreamInfo> subtitleStreams = [];
        List<AttachmentInfo> attachments = [];

        foreach (JToken stream in streams)
        {
            string codecType = stream.Value<string>("codec_type") ?? "";
            switch (codecType)
            {
                case "video":
                    videoStreams.Add(ParseVideoStream(stream));
                    break;
                case "audio":
                    audioStreams.Add(ParseAudioStream(stream));
                    break;
                case "subtitle":
                    subtitleStreams.Add(ParseSubtitleStream(stream));
                    break;
                case "attachment":
                    attachments.Add(
                        new(
                            Index: stream.Value<int>("index"),
                            Codec: stream.Value<string>("codec_name") ?? "unknown",
                            Filename: stream["tags"]?.Value<string>("filename"),
                            MimeType: stream["tags"]?.Value<string>("mimetype")
                        )
                    );
                    break;
            }
        }

        List<ChapterInfo> chapterList = [];
        foreach (JToken chapter in chapters)
        {
            chapterList.Add(
                new(
                    Start: TimeSpan.FromSeconds(chapter.Value<double>("start_time")),
                    End: TimeSpan.FromSeconds(chapter.Value<double>("end_time")),
                    Title: chapter["tags"]?.Value<string>("title")
                )
            );
        }

        double durationSeconds = format.Value<double>("duration");
        long bitRate = ParseLong(format, "bit_rate");
        long fileSize = ParseLong(format, "size");
        string formatName = format.Value<string>("format_name") ?? "unknown";

        DolbyVisionInfo? dolbyVision = ParseDolbyVision(streams);
        string? stereoMode = ParseStereoMode(streams);
        string? sphericalProjection = ParseSphericalProjection(streams);

        return new(
            FilePath: filePath,
            Format: formatName,
            Duration: TimeSpan.FromSeconds(durationSeconds),
            OverallBitRateKbps: bitRate / 1000,
            FileSizeBytes: fileSize,
            VideoStreams: videoStreams,
            AudioStreams: audioStreams,
            SubtitleStreams: subtitleStreams,
            Chapters: chapterList,
            Attachments: attachments,
            DolbyVision: dolbyVision,
            StereoMode: stereoMode,
            SphericalProjection: sphericalProjection,
            // Already parsed above — retained verbatim, never re-parsed, so
            // the reconstruction blueprint's source.ffprobe is byte-identical
            // to what ffprobe emitted.
            Ffprobe: root
        );
    }

    internal static string? ParseStereoMode(JArray streams)
    {
        foreach (JToken stream in streams)
        {
            if (stream.Value<string>("codec_type") != "video")
                continue;

            string? tagValue = stream["tags"]?.Value<string>("stereo_mode");
            if (!string.IsNullOrEmpty(tagValue))
                return tagValue;

            JArray? sideData = stream["side_data_list"] as JArray;
            if (sideData is null)
                continue;

            foreach (JToken entry in sideData)
            {
                string? sideDataType = entry.Value<string>("side_data_type");
                if (
                    sideDataType is null
                    || !sideDataType.Contains("Stereo 3D", StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                string? mode = entry.Value<string>("type");
                if (!string.IsNullOrEmpty(mode))
                    return mode;
            }
        }

        return null;
    }

    internal static string? ParseSphericalProjection(JArray streams)
    {
        foreach (JToken stream in streams)
        {
            if (stream.Value<string>("codec_type") != "video")
                continue;

            JArray? sideData = stream["side_data_list"] as JArray;
            if (sideData is null)
                continue;

            foreach (JToken entry in sideData)
            {
                string? sideDataType = entry.Value<string>("side_data_type");
                if (
                    sideDataType is null
                    || !sideDataType.Contains(
                        "Spherical Mapping",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    continue;

                string? projection = entry.Value<string>("projection");
                if (!string.IsNullOrEmpty(projection))
                    return projection;
            }
        }

        return null;
    }

    private static VideoStreamInfo ParseVideoStream(JToken stream)
    {
        string pixFmt = stream.Value<string>("pix_fmt") ?? "yuv420p";
        int bitDepth = ParseBitDepth(stream, pixFmt);
        double frameRate = ParseFrameRate(stream.Value<string>("r_frame_rate") ?? "24/1");
        double? avgFrameRate = ParseNullableFrameRate(stream.Value<string>("avg_frame_rate"));
        double? realFrameRate = ParseNullableFrameRate(stream.Value<string>("r_frame_rate"));

        return new(
            Index: stream.Value<int>("index"),
            Codec: stream.Value<string>("codec_name") ?? "unknown",
            Width: stream.Value<int>("width"),
            Height: stream.Value<int>("height"),
            FrameRate: frameRate,
            BitDepth: bitDepth,
            PixelFormat: pixFmt,
            ColorPrimaries: stream.Value<string>("color_primaries"),
            ColorTransfer: stream.Value<string>("color_transfer"),
            ColorSpace: stream.Value<string>("color_space"),
            IsDefault: stream["disposition"]?.Value<int>("default") == 1,
            BitRateKbps: ParseStreamBitrateKbps(stream),
            AverageFrameRate: avgFrameRate,
            RealFrameRate: realFrameRate,
            FieldOrder: stream.Value<string>("field_order"),
            SampleAspectRatio: stream.Value<string>("sample_aspect_ratio")
        );
    }

    /// <summary>
    /// Walks every video stream's <c>side_data_list</c> looking for the
    /// "DOVI configuration record" entry that ffprobe emits for Dolby Vision
    /// content. Returns <c>null</c> when no DV record is present. Only the
    /// first DV-bearing stream is reported — a single file never has more
    /// than one DV profile in practice.
    /// </summary>
    internal static DolbyVisionInfo? ParseDolbyVision(JArray streams)
    {
        foreach (JToken stream in streams)
        {
            if (stream.Value<string>("codec_type") != "video")
                continue;

            JArray? sideData = stream["side_data_list"] as JArray;
            if (sideData is null)
                continue;

            foreach (JToken entry in sideData)
            {
                string? type = entry.Value<string>("side_data_type");
                // ffprobe uses two spellings depending on how DV is muxed:
                // "DOVI configuration record" (MP4/MKV container) and
                // "Dolby Vision Metadata" (in-stream RPU).
                if (
                    type is null
                    || (
                        !type.Contains("DOVI", StringComparison.OrdinalIgnoreCase)
                        && !type.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase)
                    )
                )
                    continue;

                int profile = entry.Value<int?>("dv_profile") ?? 0;
                int level = entry.Value<int?>("dv_level") ?? 0;
                bool hasRpu = entry.Value<int?>("rpu_present_flag") == 1;
                bool hasEl = entry.Value<int?>("el_present_flag") == 1;
                int compatId = entry.Value<int?>("dv_bl_signal_compatibility_id") ?? 0;

                DvBlCompatibility compat = compatId switch
                {
                    1 => DvBlCompatibility.Hdr10,
                    2 => DvBlCompatibility.Sdr,
                    _ => DvBlCompatibility.None,
                };

                return new(profile, level, hasRpu, hasEl, compat);
            }
        }

        return null;
    }

    private static AudioStreamInfo ParseAudioStream(JToken stream)
    {
        return new(
            Index: stream.Value<int>("index"),
            Codec: stream.Value<string>("codec_name") ?? "unknown",
            Channels: stream.Value<int>("channels"),
            SampleRate: stream.Value<int>("sample_rate"),
            BitRateKbps: ParseStreamBitrateKbps(stream),
            Language: stream["tags"]?.Value<string>("language"),
            IsDefault: stream["disposition"]?.Value<int>("default") == 1,
            IsForced: stream["disposition"]?.Value<int>("forced") == 1,
            Profile: stream.Value<string>("profile"),
            ChannelLayout: stream.Value<string>("channel_layout")
        );
    }

    private static SubtitleStreamInfo ParseSubtitleStream(JToken stream)
    {
        return new(
            Index: stream.Value<int>("index"),
            Codec: stream.Value<string>("codec_name") ?? "unknown",
            Language: stream["tags"]?.Value<string>("language"),
            IsDefault: stream["disposition"]?.Value<int>("default") == 1,
            IsForced: stream["disposition"]?.Value<int>("forced") == 1,
            Title: stream["tags"]?.Value<string>("title")
        );
    }

    private static int ParseBitDepth(JToken stream, string pixFmt)
    {
        string? bitsRaw = stream.Value<string>("bits_per_raw_sample");
        if (bitsRaw is not null && int.TryParse(bitsRaw, out int bits))
            return bits;
        return pixFmt.Contains("10") ? 10 : 8;
    }

    private static double ParseFrameRate(string frac)
    {
        string[] parts = frac.Split('/');
        if (
            parts.Length == 2
            && double.TryParse(
                parts[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double num
            )
            && double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double den
            )
            && den > 0
        )
            return num / den;
        return 24.0;
    }

    private static double? ParseNullableFrameRate(string? frac)
    {
        if (frac is null)
            return null;
        string[] parts = frac.Split('/');
        if (
            parts.Length == 2
            && double.TryParse(
                parts[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double num
            )
            && double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double den
            )
            && den > 0
        )
            return num / den;
        return null;
    }

    private static long ParseLong(JToken token, string key)
    {
        string? val = token.Value<string>(key);
        return val is not null && long.TryParse(val, out long result) ? result : 0;
    }

    /// <summary>
    /// A stream's bitrate, in kbps, from whichever place the muxer recorded it.
    /// Matroska does not write <c>bit_rate</c> on its streams, so reading only
    /// that field reports 0 kbps for most of a typical library — and 0 does not
    /// read as "unknown" downstream, it reads as "no bits": the ABR ladder skips
    /// its never-upsource and source-percentage rules, a bandwidth-capped client
    /// is told any file fits, and smart-copy sees nothing to compare against.
    /// mkvmerge records the real figure in the per-stream statistics tags, so
    /// take BPS when it is there and derive it from byte count over duration
    /// when it is not. 0 is returned only when the file genuinely says nothing.
    /// </summary>
    internal static long ParseStreamBitrateKbps(JToken stream)
    {
        long declared = ParseLong(stream, "bit_rate");
        if (declared > 0)
            return declared / 1000;

        JToken? tags = stream["tags"];
        if (tags is null)
            return 0;

        long bitsPerSecond = ParseTagLong(tags, "BPS");
        if (bitsPerSecond > 0)
            return bitsPerSecond / 1000;

        long bytes = ParseTagLong(tags, "NUMBER_OF_BYTES");
        double seconds = ParseTagDurationSeconds(tags, "DURATION");
        if (bytes > 0 && seconds > 0)
            return (long)(bytes * 8 / seconds / 1000);

        return 0;
    }

    /// <summary>
    /// Reads a statistics tag, tolerating the language-suffixed spelling
    /// (<c>BPS-eng</c>) that mkvmerge writes when the track declares a language.
    /// Tag names are matched case-insensitively; ffprobe preserves the case the
    /// muxer used and that is not guaranteed to be upper.
    /// </summary>
    private static JToken? FindTag(JToken tags, string name)
    {
        foreach (JProperty property in tags.Children<JProperty>())
        {
            if (
                property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || property.Name.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)
            )
                return property.Value;
        }

        return null;
    }

    private static long ParseTagLong(JToken tags, string name)
    {
        string? val = FindTag(tags, name)?.Value<string>();
        return val is not null && long.TryParse(val, out long result) ? result : 0;
    }

    /// <summary>Parses the <c>HH:MM:SS.fffffffff</c> form the statistics tags use.</summary>
    private static double ParseTagDurationSeconds(JToken tags, string name)
    {
        string? val = FindTag(tags, name)?.Value<string>();
        if (val is null)
            return 0;

        string[] parts = val.Split(':');
        if (parts.Length != 3)
            return 0;

        return
            double.TryParse(
                parts[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double hours
            )
            && double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double minutes
            )
            && double.TryParse(
                parts[2],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double seconds
            )
            ? hours * 3600 + minutes * 60 + seconds
            : 0;
    }
}
