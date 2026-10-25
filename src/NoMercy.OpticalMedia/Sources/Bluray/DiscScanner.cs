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
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Infrastructure;
using NoMercy.NmSystem.Dto;

namespace NoMercy.OpticalMedia.Sources.Bluray;

/// <summary>
/// Enumerates titles on a Blu-ray or DVD by invoking FFprobe against the
/// disc's libbluray / libdvdread pseudo-URL. Returns a <see cref="DiscInfo"/>
/// with every playable title the user can then select for ripping.
///
/// Drive path conventions:
/// - Linux Blu-ray: <c>bluray:/dev/sr0</c>
/// - Linux DVD:     <c>/dev/sr0</c> (ffprobe auto-detects the VIDEO_TS tree)
/// - Windows:       <c>bluray:D:</c> or <c>D:\</c>
///
/// The caller passes whichever form the OS yielded — the scanner doesn't
/// rewrite paths. <see cref="IDriveMonitor"/> produces usable paths.
/// </summary>
public partial class DiscScanner(
    EncoderOptions options,
    IProcessRunner processRunner,
    ILogger<DiscScanner> logger
)
{
    public async Task<DiscInfo> ScanAsync(string drivePath, CancellationToken ct)
    {
        OpticalDiscType discType = drivePath.StartsWith(
            "bluray:",
            StringComparison.OrdinalIgnoreCase
        )
            ? OpticalDiscType.BluRay
            : OpticalDiscType.Dvd;

        // For Blu-ray drives, run a 1-second ffprobe pre-scan to detect AACS /
        // BD+ failures before attempting the full (potentially slow) scan.
        if (discType == OpticalDiscType.BluRay)
        {
            using CancellationTokenSource probeCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(1));

            try
            {
                ProcessResult preProbe = await processRunner.RunAsync(
                    options.FfprobePath,
                    ["-v", "quiet", "-show_format", drivePath],
                    workingDirectory: null,
                    cancellationToken: probeCts.Token
                );

                if (!preProbe.IsSuccess)
                    ClassifyBluRayStderr(drivePath, preProbe.StdErr);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 1-second timeout expired — process still running means the disc
                // responded (just slowly). Let the real scan proceed.
            }
        }

        string[] args =
        [
            "-v",
            "quiet",
            "-print_format",
            "json",
            "-show_format",
            "-show_streams",
            "-show_chapters",
            drivePath,
        ];

        ProcessResult result = await processRunner.RunAsync(
            options.FfprobePath,
            args,
            workingDirectory: null,
            cancellationToken: ct
        );

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StdOut))
        {
            logger.LogWarning(
                "Disc scan failed for {Drive} (exit {Exit}): {Stderr}",
                drivePath,
                result.ExitCode,
                TrimStderr(result.StdErr)
            );
            return new(discType, null, [], null, TimeSpan.Zero);
        }

        try
        {
            return Parse(result.StdOut, discType);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse disc scan output for {Drive}", drivePath);
            return new(discType, null, [], null, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Parses the ffprobe JSON envelope for a single title/playlist probe.
    /// A lone title has no visibility into its siblings' durations, so
    /// <see cref="DiscTitle.IsMainFeature"/> always comes back <c>false</c>
    /// here — the caller (a per-title probe against a real drive) is
    /// responsible for ranking it against the disc's other titles and
    /// re-stamping the field, the same way <c>BlurayDiscSource</c> and
    /// <c>DvdDiscSource</c> already do.
    /// </summary>
    internal static DiscInfo Parse(string json, OpticalDiscType discType)
    {
        // ffprobe on a partially-readable disc occasionally returns truncated
        // output — surface that as InvalidOperationException so the caller
        // can log meaningfully instead of letting JsonException leak out.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"ffprobe output was not valid JSON: {ex.Message}",
                ex
            );
        }
        using JsonDocument _ = doc;
        JsonElement root = doc.RootElement;

        string? discLabel = null;
        TimeSpan duration = TimeSpan.Zero;
        if (root.TryGetProperty("format", out JsonElement format))
        {
            if (
                format.TryGetProperty("tags", out JsonElement tags)
                && tags.TryGetProperty("title", out JsonElement titleElement)
            )
            {
                discLabel = titleElement.GetString();
            }
            if (
                format.TryGetProperty("duration", out JsonElement durationElement)
                && double.TryParse(
                    durationElement.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double seconds
                )
            )
            {
                duration = TimeSpan.FromSeconds(seconds);
            }
        }

        List<VideoStreamInfo> videoStreams = [];
        List<AudioStreamInfo> audioStreams = [];
        List<SubtitleStreamInfo> subtitles = [];
        if (root.TryGetProperty("streams", out JsonElement streams))
        {
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                string codecType = stream.TryGetProperty("codec_type", out JsonElement t)
                    ? (t.GetString() ?? "")
                    : "";

                switch (codecType)
                {
                    case "video":
                        videoStreams.Add(ParseVideo(stream));
                        break;
                    case "audio":
                        audioStreams.Add(ParseAudio(stream));
                        break;
                    case "subtitle":
                        subtitles.Add(ParseSubtitle(stream));
                        break;
                }
            }
        }

        List<ChapterInfo> chapters = [];
        if (root.TryGetProperty("chapters", out JsonElement chaptersElement))
        {
            foreach (JsonElement chapter in chaptersElement.EnumerateArray())
            {
                chapters.Add(ParseChapter(chapter));
            }
        }

        DiscTitle singleTitle = new(
            Index: 0,
            Name: discLabel,
            Duration: duration,
            VideoStreams: videoStreams.ToArray(),
            AudioStreams: audioStreams.ToArray(),
            Subtitles: subtitles.ToArray(),
            Chapters: chapters.ToArray(),
            EstimatedSizeBytes: 0,
            IsMainFeature: false
        );

        return new(discType, discLabel, [singleTitle], null, duration);
    }

    private static VideoStreamInfo ParseVideo(JsonElement stream) =>
        new(
            Index: stream.TryGetProperty("index", out JsonElement i) ? i.GetInt32() : 0,
            Codec: stream.TryGetProperty("codec_name", out JsonElement c)
                ? (c.GetString() ?? "")
                : "",
            Width: stream.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0,
            Height: stream.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0,
            FrameRate: 0,
            BitDepth: 8,
            PixelFormat: stream.TryGetProperty("pix_fmt", out JsonElement px)
                ? (px.GetString() ?? "")
                : "",
            ColorPrimaries: null,
            ColorTransfer: null,
            ColorSpace: null,
            IsDefault: false,
            BitRateKbps: 0
        );

    private static AudioStreamInfo ParseAudio(JsonElement stream)
    {
        string? language = null;
        if (
            stream.TryGetProperty("tags", out JsonElement tags)
            && tags.TryGetProperty("language", out JsonElement lang)
        )
        {
            language = lang.GetString();
        }

        return new(
            Index: stream.TryGetProperty("index", out JsonElement i) ? i.GetInt32() : 0,
            Codec: stream.TryGetProperty("codec_name", out JsonElement c)
                ? (c.GetString() ?? "")
                : "",
            Channels: stream.TryGetProperty("channels", out JsonElement ch) ? ch.GetInt32() : 0,
            SampleRate: stream.TryGetProperty("sample_rate", out JsonElement sr)
            && int.TryParse(sr.GetString(), out int srInt)
                ? srInt
                : 0,
            BitRateKbps: 0,
            Language: language,
            IsDefault: false,
            IsForced: false
        );
    }

    private static SubtitleStreamInfo ParseSubtitle(JsonElement stream)
    {
        string? language = null;
        if (
            stream.TryGetProperty("tags", out JsonElement tags)
            && tags.TryGetProperty("language", out JsonElement lang)
        )
        {
            language = lang.GetString();
        }

        return new(
            Index: stream.TryGetProperty("index", out JsonElement i) ? i.GetInt32() : 0,
            Codec: stream.TryGetProperty("codec_name", out JsonElement c)
                ? (c.GetString() ?? "")
                : "",
            Language: language,
            IsDefault: false,
            IsForced: false
        );
    }

    private static ChapterInfo ParseChapter(JsonElement chapter)
    {
        double start = 0;
        double end = 0;
        if (
            chapter.TryGetProperty("start_time", out JsonElement s)
            && double.TryParse(
                s.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double startParsed
            )
        )
        {
            start = startParsed;
        }
        if (
            chapter.TryGetProperty("end_time", out JsonElement e)
            && double.TryParse(
                e.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double endParsed
            )
        )
        {
            end = endParsed;
        }

        string? title = null;
        if (
            chapter.TryGetProperty("tags", out JsonElement tags)
            && tags.TryGetProperty("title", out JsonElement t)
        )
        {
            title = t.GetString();
        }

        return new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), title);
    }

    /// <summary>
    /// Inspects ffprobe stderr from a Blu-ray pre-probe and throws the
    /// appropriate <see cref="EncoderRuntimeException"/> when a known
    /// decryption failure pattern is detected.
    /// </summary>
    internal static void ClassifyBluRayStderr(string drivePath, string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return;

        // libaacs emits this when KEYDB.cfg has no entry for the disc's volume ID.
        // Pattern observed in libaacs src/libaacs/aacs.c:
        //   "aacs: no matching certificate"  (older builds)
        //   "AACS: no matching certificate"  (case varies by build)
        if (
            stderr.Contains("no matching certificate", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("aacs:", StringComparison.OrdinalIgnoreCase)
                && stderr.Contains("certificate", StringComparison.OrdinalIgnoreCase)
        )
        {
            string volumeId = ExtractVolumeId(stderr) ?? drivePath;
            throw RuntimeErrors.DiscAacsCertMissing(volumeId);
        }

        // libbdplus emits this when the converter database has no entry for the disc.
        // Pattern from libbdplus src/libbdplus/bdplus.c:
        //   "bdplus: no matching converter"
        if (stderr.Contains("no matching converter", StringComparison.OrdinalIgnoreCase))
        {
            string volumeId = ExtractVolumeId(stderr) ?? drivePath;
            throw RuntimeErrors.DiscBdplusConverterMissing(volumeId);
        }

        // Any other protocol-level read failure.
        if (
            stderr.Contains("Protocol not found", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Input/output error", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw RuntimeErrors.DiscReadError(drivePath, TrimStderr(stderr));
        }
    }

    /// <summary>
    /// Attempts to extract a volume/disc ID from ffprobe / libaacs stderr lines.
    /// libaacs typically logs the volume ID as a hex string on the error line.
    /// Returns null when no candidate is found.
    /// </summary>
    private static string? ExtractVolumeId(string stderr)
    {
        foreach (string line in stderr.Split('\n'))
        {
            // Look for a line containing a 32-char hex string — the AACS volume ID.
            Match m = VolumeIdRegex().Match(line);
            if (m.Success)
                return m.Value.ToUpperInvariant();
        }

        return null;
    }

    private static string TrimStderr(string stdErr)
    {
        if (string.IsNullOrEmpty(stdErr))
            return "(no stderr)";
        string[] lines = stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length <= 3 ? stdErr : string.Join('\n', lines[^3..]);
    }

    [GeneratedRegex(@"[0-9A-Fa-f]{32}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VolumeIdRegex();
}
