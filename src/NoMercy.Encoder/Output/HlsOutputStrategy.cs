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
using System.Text;
using System.Text.RegularExpressions;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Subtitles;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;

namespace NoMercy.Encoder.Output;

public partial class HlsOutputStrategy(IStorage storage) : IOutputStrategy
{
    public OutputFormat Format => OutputFormat.Hls;

    [GeneratedRegex(@"_\d{5}\.vtt$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SegmentFileNameRegex();

    /// <summary>
    /// Language + readable title for one audio rendition. Falls back to "und" and
    /// clears the title when the plan carries no language, so a stream is never
    /// left wearing the source's tag.
    /// </summary>
    private static IReadOnlyList<OutputStreamTag> BuildAudioStreamTags(string? language)
    {
        string code = string.IsNullOrWhiteSpace(language) ? "und" : language.Trim();
        string title = code == "und" ? string.Empty : Culture.EnglishLanguageName(code);

        return
        [
            new OutputStreamTag("s:a:0", "language", code),
            new OutputStreamTag("s:a:0", "title", title),
            new OutputStreamTag("s:a:0", "handler_name", title),
        ];
    }

    public void ConfigureOutput(
        FfmpegCommandBuilder builder,
        OutputPlan plan,
        string outputDirectory
    )
    {
        int segmentDuration = plan.SegmentDurationSeconds;
        HlsPlanOptions hlsOptions = plan.HlsOptions ?? new HlsPlanOptions();

        // Hoist segment-type derived values; both video and audio loops need them.
        bool isFmp4 = hlsOptions.SegmentType.Equals("fmp4", StringComparison.OrdinalIgnoreCase);
        string segmentExtension = isFmp4 ? ".m4s" : ".ts";

        foreach (VideoOutputPlan video in plan.VideoOutputs)
        {
            Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
                video.Width,
                video.Height,
                video.IsHdrOutput
            );

            // Template resolves to e.g. "video_1920x1080_SDR/video_1920x1080_SDR"
            string segmentResolved = TemplateResolver.Resolve(video.SegmentNameTemplate, tokens);
            string playlistResolved = TemplateResolver.Resolve(video.PlaylistNameTemplate, tokens);

            // Split into directory and filename parts using storage-aware helpers
            // (forward-slash canonical, no OS-separator contamination on Windows).
            string subDir = storage.GetParent(playlistResolved) ?? playlistResolved;
            string playlistFile = storage.GetName(playlistResolved);
            string segmentDir = storage.GetParent(segmentResolved) ?? segmentResolved;
            string segmentFile = storage.GetName(segmentResolved);

            // Paths are relative — FFmpeg CWD is set to the output directory.
            string playlistPath = $"{subDir}/{playlistFile}.m3u8";

            bool isHevc =
                video.EncoderName.Contains("265", StringComparison.OrdinalIgnoreCase)
                || video.EncoderName.Contains("hevc", StringComparison.OrdinalIgnoreCase);

            int gopCeiling = (int)Math.Ceiling(video.FrameRate * segmentDuration * 2);

            // Build hls_flags: always include independent_segments (existing behaviour).
            // When HlsOptions.IndependentSegments is true the flag is still included —
            // future phases may add additional flags joined with '+' here.
            string hlsFlags = "independent_segments";

            Dictionary<string, string> extraFlags = new(video.ExtraFlags)
            {
                ["-f"] = "hls",
                ["-hls_time"] = segmentDuration.ToString(),
                ["-hls_playlist_type"] = hlsOptions.PlaylistType,
                ["-hls_segment_type"] = hlsOptions.SegmentType,
                ["-hls_flags"] = hlsFlags,
                ["-hls_segment_filename"] = $"{segmentDir}/{segmentFile}_%05d{segmentExtension}",
                ["-force_key_frames"] = $"expr:gte(t,n_forced*{segmentDuration})",
                ["-forced-idr"] = "1",
            };

            // A variable-frame-rate source must be muxed at a constant frame rate
            // for HLS: VFR PTS gaps drift the segment boundaries away from the
            // -hls_time target and desync across an ABR switch. CFR sources are
            // unaffected (already constant), so this only reshapes VFR input.
            if (plan.NormalizeToConstantFrameRate)
                extraFlags["-fps_mode"] = "cfr";

            // fMP4 requires an init segment with a deterministic name alongside the playlist.
            if (isFmp4)
                extraFlags["-hls_fmp4_init_filename"] = "init.mp4";

            if (isHevc)
                extraFlags["-tag:v"] = "hvc1";

            // Dolby Vision overrides hvc1 — HLS/fMP4 players require dvh1 to
            // route the stream through the DV decoder path.
            if (plan.PreserveDolbyVision && isHevc)
                extraFlags["-tag:v"] = "dvh1";

            builder.AddOutput(
                new(
                    FilePath: playlistPath,
                    VideoCodec: video.EncoderName,
                    Crf: video.Crf > 0 ? video.Crf : null,
                    VideoBitrateKbps: video.BitrateKbps > 0 ? video.BitrateKbps : null,
                    Preset: video.Preset,
                    Profile: video.Profile,
                    Level: video.Level,
                    PixelFormat: video.TenBit ? video.PixelFormat : null,
                    KeyframeInterval: gopCeiling,
                    MapStreams: [video.MapLabel],
                    ExtraFlags: extraFlags,
                    StripSourceMetadata: true,
                    // A video stream has no language, and the source's title was the
                    // ripper's own tag ("[Judas] x265 10b"). Clear both so nothing
                    // from the source is presented as ours.
                    StreamMetadata:
                    [
                        new("s:v:0", "language", string.Empty),
                        new("s:v:0", "title", string.Empty),
                        new("s:v:0", "handler_name", string.Empty),
                    ]
                )
            );
        }

        foreach (AudioOutputPlan audio in plan.AudioOutputs)
        {
            if (audio.Action == StreamAction.Copy || audio.Action == StreamAction.Transcode)
            {
                Dictionary<string, string> tokens = TemplateResolver.AudioTokens(
                    audio.Language ?? "und",
                    audio.CodecToken,
                    audio.Channels
                );

                string segmentResolved = TemplateResolver.Resolve(
                    audio.SegmentNameTemplate,
                    tokens
                );
                string playlistResolved = TemplateResolver.Resolve(
                    audio.PlaylistNameTemplate,
                    tokens
                );

                string subDir = storage.GetParent(playlistResolved) ?? playlistResolved;
                string playlistFile = storage.GetName(playlistResolved);
                string segmentDir = storage.GetParent(segmentResolved) ?? segmentResolved;
                string segmentFile = storage.GetName(segmentResolved);

                string playlistPath = $"{subDir}/{playlistFile}.m3u8";

                Dictionary<string, string> extraFlags = new()
                {
                    ["-f"] = "hls",
                    ["-hls_time"] = segmentDuration.ToString(),
                    ["-hls_playlist_type"] = hlsOptions.PlaylistType,
                    ["-hls_segment_type"] = hlsOptions.SegmentType,
                    ["-hls_flags"] = "independent_segments",
                    ["-hls_segment_filename"] =
                        $"{segmentDir}/{segmentFile}_%05d{segmentExtension}",
                };

                if (
                    audio.Action == StreamAction.Transcode
                    && !string.IsNullOrEmpty(audio.AudioFilter)
                )
                {
                    extraFlags["-af"] = audio.AudioFilter;
                }

                // Per-audio CustomArguments escape hatch — applied last so author
                // intent wins (validator already blocks codec/format keys).
                if (audio.ExtraFlags is not null)
                {
                    foreach ((string key, string value) in audio.ExtraFlags)
                        extraFlags[key] = value;
                }

                string audioCodec = audio.Action == StreamAction.Copy ? "copy" : audio.EncoderName;

                builder.AddOutput(
                    new(
                        FilePath: playlistPath,
                        AudioCodec: audioCodec,
                        AudioBitrateKbps: audio.Action == StreamAction.Transcode
                            ? audio.BitrateKbps
                            : null,
                        AudioChannels: audio.Channels.ToString(),
                        AudioSampleRate: audio.SampleRate,
                        MapStreams: [audio.MapLabel],
                        ExtraFlags: extraFlags,
                        StripSourceMetadata: true,
                        // Re-tag with our own values rather than carry the ripper's
                        // ("[Judas] JAP Stereo (Opus 112Kbps)"): the language we
                        // planned this rendition around, and a readable name derived
                        // from it. An unknown language clears both.
                        StreamMetadata: BuildAudioStreamTags(audio.Language)
                    )
                );
            }
        }
    }

    public async Task FinalizeAsync(
        string outputDirectory,
        OutputPlan plan,
        string mediaTitle,
        CancellationToken ct
    )
    {
        // Measure actual bitrates from the encoded variant playlists.
        // These are the real values — not estimates from profile settings.
        HlsVariantAnalyzer analyzer = new(storage);
        List<string> measuredVariantPaths = [];
        Dictionary<string, VariantMetrics> videoMetrics = [];
        foreach (VideoOutputPlan video in plan.VideoOutputs)
        {
            Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
                video.Width,
                video.Height,
                video.IsHdrOutput
            );
            string playlistResolved = TemplateResolver.Resolve(video.PlaylistNameTemplate, tokens);
            string subDir = storage.GetParent(playlistResolved) ?? playlistResolved;
            string playlistFile = storage.GetName(playlistResolved);
            string variantPath = storage.CombinePath(
                storage.CombinePath(outputDirectory, subDir),
                $"{playlistFile}.m3u8"
            );

            measuredVariantPaths.Add(variantPath);
            // Key by the variant's resolved playlist path, NOT MapLabel: every
            // rung re-plans as "[v0]" in its own bundle, so keying by MapLabel
            // collapses every variant onto one entry and the master advertises a
            // single shared BANDWIDTH for all resolutions. The resolved path is
            // unique per variant (width/height/HDR), and PlaylistGenerator looks
            // up by the same key.
            videoMetrics[playlistResolved] = analyzer.Measure(variantPath);
        }

        Dictionary<string, VariantMetrics> audioMetrics = [];
        foreach (AudioOutputPlan audio in plan.AudioOutputs)
        {
            if (audio.Action is not (StreamAction.Copy or StreamAction.Transcode))
                continue;

            Dictionary<string, string> tokens = TemplateResolver.AudioTokens(
                audio.Language ?? "und",
                audio.CodecToken,
                audio.Channels
            );
            string playlistResolved = TemplateResolver.Resolve(audio.PlaylistNameTemplate, tokens);
            string subDir = storage.GetParent(playlistResolved) ?? playlistResolved;
            string playlistFile = storage.GetName(playlistResolved);
            string variantPath = storage.CombinePath(
                storage.CombinePath(outputDirectory, subDir),
                $"{playlistFile}.m3u8"
            );

            measuredVariantPaths.Add(variantPath);
            // Same reasoning as video: key by the resolved playlist path so
            // multiple audio renditions never collide on a shared MapLabel.
            audioMetrics[playlistResolved] = analyzer.Measure(variantPath);
        }

        // A master that lists zero variants is unplayable; writing it would also
        // clobber a previously good master when several presets share one output
        // directory. Fail loudly instead — FinalizeStage surfaces this as a
        // stage failure with the exact paths that came up empty.
        bool anyVariantMeasured =
            videoMetrics.Values.Any(metrics => metrics.PeakBandwidth > 0)
            || audioMetrics.Values.Any(metrics => metrics.PeakBandwidth > 0);

        if (measuredVariantPaths.Count > 0 && !anyVariantMeasured)
        {
            List<string> missing = [.. measuredVariantPaths.Where(p => !storage.Exists(p))];
            List<string> empty =
            [
                .. measuredVariantPaths.Where(p =>
                    storage.Exists(p) && analyzer.Measure(p).PeakBandwidth == 0
                ),
            ];

            throw new InvalidOperationException(
                "Master playlist would list zero variants — no variant playlist produced "
                    + $"measurable segments. Output dir: {outputDirectory}. "
                    + $"Missing: {string.Join(", ", missing)}. "
                    + $"Empty/Invalid: {string.Join(", ", empty)}."
            );
        }

        // Build subtitle sidecars first so the master playlist only advertises
        // subtitle URIs that actually exist on disk.
        await WriteSubtitleSidecarsAsync(outputDirectory, plan, ct);

        OutputPlan masterPlan = BuildMasterPlaylistPlan(outputDirectory, plan);

        PlaylistGenerator generator = new();
        string masterPlaylist = generator.GenerateMasterPlaylist(
            masterPlan,
            mediaTitle,
            videoMetrics,
            audioMetrics
        );
        string masterPath = storage.CombinePath(outputDirectory, $"{mediaTitle}.m3u8");
        await storage.WriteAsync(masterPath, Encoding.UTF8.GetBytes(masterPlaylist), ct);
    }

    private OutputPlan BuildMasterPlaylistPlan(string outputDirectory, OutputPlan plan)
    {
        SubtitleOutputPlan[] existingSubtitleOutputs =
        [
            .. plan
                .SubtitleOutputs.Where(s => s.Action is StreamAction.Extract or StreamAction.Copy)
                .Where(s =>
                {
                    string relativeUri = PlaylistGenerator.GetSubtitlePlaylistUri(s);
                    string absolutePath = storage.CombinePath(outputDirectory, relativeUri);
                    return storage.Exists(absolutePath);
                }),
        ];

        return plan with
        {
            SubtitleOutputs = existingSubtitleOutputs,
        };
    }

    // Slices each extracted WebVTT into per-segment .vtt files matching the
    // video segment cadence (RFC 8216 §3.5) and emits the playlist that
    // references them. ASS/SRT URIs in the master point straight at the
    // file, so those plans skip this path entirely.
    private async Task WriteSubtitleSidecarsAsync(
        string outputDirectory,
        OutputPlan plan,
        CancellationToken ct
    )
    {
        // Profile opted out of WebVTT chunking (HlsDerivatives.SubtitleWebVtt=false).
        // The raw .vtt extracts still live in subtitles/ for download; the master
        // playlist's GetSubtitlePlaylistUri returns the bare file URI in that case
        // so the master stays internally consistent.
        if (!plan.EmitSubtitleWebVttChunks)
            return;

        SubtitleOutputPlan[] webVttSubs =
        [
            .. plan.SubtitleOutputs.Where(s =>
                s.Action is StreamAction.Extract or StreamAction.Copy
                && s.OutputCodec is SubtitleCodecType.WebVtt
            ),
        ];
        if (webVttSubs.Length == 0)
            return;

        string subtitlesDir = storage.CombinePath(outputDirectory, "subtitles");
        if (!storage.Exists(subtitlesDir))
            return;

        string[] vttFiles =
        [
            .. storage
                .List(subtitlesDir, "*.vtt", recursive: false)
                .Where(e => !e.IsDirectory)
                .Select(e => e.Path),
        ];
        if (vttFiles.Length == 0)
            return;

        int segmentDurationSeconds =
            plan.SegmentDurationSeconds > 0 ? plan.SegmentDurationSeconds : 6;
        TimeSpan segmentDuration = TimeSpan.FromSeconds(segmentDurationSeconds);

        WebVttSegmenter segmenter = new();

        foreach (SubtitleOutputPlan sub in webVttSubs)
        {
            string lang = sub.Language ?? "und";
            string variant = string.IsNullOrEmpty(sub.Variant) ? "full" : sub.Variant;

            // Idempotent: when a rescan reconstructs an already-published output
            // the chunk playlist + segments are already on disk. Re-slicing would
            // redo work (and needlessly hammer the NAS for every track); skip it
            // and let the master keep advertising the existing playlist.
            string existingPlaylistPath = storage.CombinePath(
                storage.CombinePath(subtitlesDir, lang),
                $"{variant}.m3u8"
            );
            if (storage.Exists(existingPlaylistPath))
                continue;

            // Match the source .vtt the extractor produced.
            string? sourceVttPath = vttFiles.FirstOrDefault(f =>
                storage
                    .GetName(f)
                    .Contains($".{lang}.{variant}.", StringComparison.OrdinalIgnoreCase)
            );
            sourceVttPath ??= vttFiles.FirstOrDefault(f =>
                storage.GetName(f).Contains($".{lang}.", StringComparison.OrdinalIgnoreCase)
            );
            if (sourceVttPath is null)
                continue;

            // Skip our own segment files if the language probe finds them
            // before the source — they end with _NNNNN.vtt.
            if (SegmentFileNameRegex().IsMatch(storage.GetName(sourceVttPath)))
                continue;

            string vttContent = Encoding.UTF8.GetString(storage.Read(sourceVttPath));
            IReadOnlyList<WebVttSegment> segments = segmenter.SliceContent(
                vttContent,
                segmentDuration
            );

            // Chunks land in a per-language subfolder so a season with 20+
            // tracks doesn't dump 800 segment files into the root subtitles/
            // dir. Segment URIs in the media playlist are relative to that
            // folder so they stay short.
            string languageDir = storage.CombinePath(subtitlesDir, lang);
            if (!storage.Exists(languageDir))
                storage.CreateDirectory(languageDir);

            for (int i = 0; i < segments.Count; i++)
            {
                string segFile = $"{variant}_{i:D5}.vtt";
                string segPath = storage.CombinePath(languageDir, segFile);
                await storage.WriteAsync(segPath, Encoding.UTF8.GetBytes(segments[i].Content), ct);
            }

            string playlist = PlaylistGenerator.GenerateSubtitleMediaPlaylist(
                sub,
                segments,
                segmentDurationSeconds
            );
            string playlistPath = storage.CombinePath(languageDir, $"{variant}.m3u8");
            await storage.WriteAsync(playlistPath, Encoding.UTF8.GetBytes(playlist), ct);
        }
    }

    /// <summary>
    /// Sums #EXTINF durations from the first video variant playlist as the
    /// authoritative content length. Falls back to 0 if no variant is found.
    /// </summary>
    private double MeasureVideoDuration(string outputDirectory, OutputPlan plan)
    {
        VideoOutputPlan? firstVideo = plan.VideoOutputs.FirstOrDefault();
        if (firstVideo is null)
            return 0;

        Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
            firstVideo.Width,
            firstVideo.Height,
            firstVideo.IsHdrOutput
        );
        string resolved = TemplateResolver.Resolve(firstVideo.PlaylistNameTemplate, tokens);
        string subDir = storage.GetParent(resolved) ?? resolved;
        string playlistFile = storage.GetName(resolved);
        string variantPath = storage.CombinePath(
            storage.CombinePath(outputDirectory, subDir),
            $"{playlistFile}.m3u8"
        );

        if (!storage.Exists(variantPath))
            return 0;

        string content = Encoding.UTF8.GetString(storage.Read(variantPath));
        double total = 0;
        foreach (string line in content.Split('\n'))
        {
            if (!line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                continue;
            int comma = line.IndexOf(',');
            string value = comma > 8 ? line[8..comma] : line[8..];
            if (
                double.TryParse(
                    value.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double seconds
                )
            )
                total += seconds;
        }

        return total;
    }

    public string[] GetOutputSubdirectories(OutputPlan plan)
    {
        List<string> dirs = [];

        foreach (VideoOutputPlan video in plan.VideoOutputs)
        {
            Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
                video.Width,
                video.Height,
                video.IsHdrOutput
            );
            string resolved = TemplateResolver.Resolve(video.PlaylistNameTemplate, tokens);
            string subDir = storage.GetParent(resolved) ?? resolved;
            dirs.Add(subDir);
        }

        foreach (AudioOutputPlan audio in plan.AudioOutputs)
        {
            if (audio.Action is StreamAction.Copy or StreamAction.Transcode)
            {
                Dictionary<string, string> tokens = TemplateResolver.AudioTokens(
                    audio.Language ?? "und",
                    audio.CodecToken,
                    audio.Channels
                );
                string resolved = TemplateResolver.Resolve(audio.PlaylistNameTemplate, tokens);
                string subDir = storage.GetParent(resolved) ?? resolved;
                dirs.Add(subDir);
            }
        }

        return [.. dirs];
    }
}
