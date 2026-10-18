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
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Subtitles;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;

namespace NoMercy.Encoder.Output;

public class PlaylistGenerator : IPlaylistGenerator
{
    private readonly record struct AudioMediaEntry(
        string Uri,
        string Language,
        string DisplayName,
        bool IsSourceDefault,
        AudioOutputPlan Plan
    );

    /// <summary>
    /// Returns the minimum HLS version number required for the given combination
    /// of active features. The caller takes the max of all that apply.
    ///
    /// Rules (per the HLS specification):
    ///   v3 — baseline MPEG-TS (no subtitles group, no fMP4, no chapter date-ranges).
    ///   v6 — at least one EXT-X-MEDIA:TYPE=SUBTITLES group in the master playlist.
    ///   v7 — any variant uses fMP4 segments (hls_segment_type fmp4).
    ///   v8 — EXT-X-DATERANGE entries driven by chapter data (Phase 4.5).
    /// </summary>
    internal static int ComputeMasterVersion(
        bool hasSubsGroup,
        bool hasFmp4,
        bool hasChapterDateRanges
    )
    {
        int version = 3;
        if (hasSubsGroup)
            version = Math.Max(version, 6);
        if (hasFmp4)
            version = Math.Max(version, 7);
        if (hasChapterDateRanges)
            version = Math.Max(version, 8);
        return version;
    }

    /// <summary>
    /// The metrics-dict key for a video variant: its resolved playlist path,
    /// unique per resolution/HDR. MUST be used by whoever POPULATES the metrics
    /// dict (HlsOutputStrategy) and whoever LOOKS UP (this generator), so a rung
    /// that re-plans as MapLabel "[v0]" in its own bundle never collides with
    /// another variant and collapses the whole ladder onto one BANDWIDTH.
    /// </summary>
    public static string VideoVariantKey(VideoOutputPlan video) =>
        TemplateResolver.Resolve(
            video.PlaylistNameTemplate,
            TemplateResolver.VideoTokens(video.Width, video.Height, video.IsHdrOutput)
        );

    /// <summary>The audio-variant metrics key — same rationale as <see cref="VideoVariantKey"/>.</summary>
    public static string AudioVariantKey(AudioOutputPlan audio) =>
        TemplateResolver.Resolve(
            audio.PlaylistNameTemplate,
            TemplateResolver.AudioTokens(audio.Language ?? "und", audio.CodecToken, audio.Channels)
        );

    public string GenerateMasterPlaylist(
        OutputPlan plan,
        string mediaTitle,
        Dictionary<string, VariantMetrics> videoMetrics,
        Dictionary<string, VariantMetrics> audioMetrics
    )
    {
        HlsPlanOptions hlsOptions = plan.HlsOptions ?? new HlsPlanOptions();

        bool hasFmp4 = hlsOptions.SegmentType.Equals("fmp4", StringComparison.OrdinalIgnoreCase);
        // WebVTT outputs only contribute a subs group when the slicer is
        // going to emit the m3u8 wrapper they need. With chunking off the
        // .vtt extracts still exist but there is no playable HLS playlist
        // entry to advertise — referencing the raw .vtt would 404 on
        // spec-compliant clients.
        bool hasSubsGroup = plan.SubtitleOutputs.Any(s =>
            s.Action is StreamAction.Extract or StreamAction.Copy
            && (s.OutputCodec is not SubtitleCodecType.WebVtt || plan.EmitSubtitleWebVttChunks)
        );

        bool hasChapterDateRanges = plan.Chapters is { Count: > 0 };

        int version = ComputeMasterVersion(hasSubsGroup, hasFmp4, hasChapterDateRanges);

        StringBuilder sb = new();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine($"#EXT-X-VERSION:{version}");

        // Emit #EXT-X-INDEPENDENT-SEGMENTS when the option is on OR when fMP4
        // segments are used (fMP4 requires independent segments per the spec).
        if (hlsOptions.IndependentSegments || hasFmp4)
            sb.AppendLine("#EXT-X-INDEPENDENT-SEGMENTS");

        sb.AppendLine();

        // Collect the renditions that will actually be written before any line
        // is emitted. DEFAULT=YES must go to the source's default-disposition
        // track, and that track is not necessarily the first one that survives
        // the materialisation checks below.
        List<AudioMediaEntry> audioEntries = [];
        foreach (AudioOutputPlan audio in plan.AudioOutputs)
        {
            if (audio.Action is not (StreamAction.Copy or StreamAction.Transcode))
                continue;

            // Resolve this rendition's playlist path first — it is the metrics
            // key (keyed by path, NOT MapLabel, so multiple audio renditions
            // never collide on a shared label).
            Dictionary<string, string> tokens = TemplateResolver.AudioTokens(
                audio.Language ?? "und",
                audio.CodecToken,
                audio.Channels
            );

            string playlistResolved = TemplateResolver.Resolve(audio.PlaylistNameTemplate, tokens);

            // Skip audio variants whose segments never materialised. The
            // analyzer returns zero bandwidth when the playlist or its
            // segments are missing on disk — listing those in the master
            // makes hls.js / VLC bail on the first variant fetch.
            VariantMetrics audMetrics = audioMetrics.GetValueOrDefault(playlistResolved, new(0, 0));
            if (audMetrics.PeakBandwidth == 0)
                continue;

            string subDir = StoragePathHelpers.GetParent(playlistResolved) ?? playlistResolved;
            string playlistFile = StoragePathHelpers.GetName(playlistResolved);
            string language = audio.Language ?? "und";

            audioEntries.Add(
                new(
                    Uri: $"{subDir}/{playlistFile}.m3u8",
                    Language: language,
                    DisplayName: GetAudioDisplayName(language),
                    IsSourceDefault: audio.IsSourceDefault,
                    Plan: audio
                )
            );
        }

        // One audio group per codec, in first-appearance order, so a client that
        // cannot decode one codec can pick the variants of another by CODECS.
        List<string> audioGroupIds =
        [
            .. audioEntries.Select(entry => $"audio_{entry.Plan.CodecToken}").Distinct(),
        ];
        bool multipleAudioGroups = audioGroupIds.Count > 1;

        // Tracks whether any EXT-X-MEDIA:TYPE=AUDIO line was actually written —
        // NOT just whether plan.AudioOutputs is non-empty. A rendition can be
        // planned but never materialise (missing segments, zero bandwidth) and
        // gets skipped above; the STREAM-INF AUDIO="..." attribute must follow
        // that same fate so it never references a group with zero members.
        bool audioGroupEmitted = audioEntries.Count > 0;
        foreach (string groupId in audioGroupIds)
        {
            List<AudioMediaEntry> groupEntries =
            [
                .. audioEntries.Where(entry => $"audio_{entry.Plan.CodecToken}" == groupId),
            ];
            int defaultAudioIndex = groupEntries.FindIndex(entry => entry.IsSourceDefault);
            if (defaultAudioIndex < 0)
                defaultAudioIndex = 0;

            for (int i = 0; i < groupEntries.Count; i++)
            {
                AudioMediaEntry entry = groupEntries[i];
                sb.AppendLine(
                    $"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"{groupId}\",LANGUAGE=\"{entry.Language}\",AUTOSELECT=YES,DEFAULT={YesNo(i == defaultAudioIndex)},URI=\"{entry.Uri}\",NAME=\"{entry.DisplayName}\""
                );
            }
        }

        // Subtitle groups — one EXT-X-MEDIA per plan. URI now reflects
        // language + variant + codec, so the "English (Full)" / "(Sign)" /
        // "(SDH)" tracks of a single source land as three distinct entries
        // pointing at three distinct files instead of collapsing to one.
        // WebVTT entries are filtered out when chunking is disabled — same
        // reasoning as hasSubsGroup above.
        SubtitleOutputPlan[] activeSubs =
        [
            .. plan.SubtitleOutputs.Where(s =>
                s.Action is StreamAction.Extract or StreamAction.Copy
                && (s.OutputCodec is not SubtitleCodecType.WebVtt || plan.EmitSubtitleWebVttChunks)
            ),
        ];

        if (activeSubs.Length > 0)
        {
            foreach (SubtitleOutputPlan sub in activeSubs)
            {
                string lang = sub.Language ?? "und";
                string displayName = GetSubtitleDisplayName(lang, sub.Variant);
                string subsUri = GetSubtitlePlaylistUri(sub);
                bool isForced = string.Equals(
                    sub.Variant,
                    "sign",
                    StringComparison.OrdinalIgnoreCase
                );

                sb.AppendLine(
                    $"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"{displayName}\",LANGUAGE=\"{lang}\",DEFAULT=NO,AUTOSELECT=YES,FORCED={YesNo(isForced)},URI=\"{subsUri}\""
                );
            }

            sb.AppendLine();
        }

        // Video variants with measured bandwidth, group-major: every video for
        // the first audio group, then every video for the next, so the head of
        // the master matches what single-codec masters always listed.
        List<string?> variantGroupIds = audioGroupEmitted ? [.. audioGroupIds] : [null];
        foreach (string? audioGroupId in variantGroupIds)
        foreach (VideoOutputPlan video in plan.VideoOutputs)
        {
            AudioOutputPlan? primaryAudio = audioGroupId is null
                ? plan.AudioOutputs.FirstOrDefault()
                : plan.AudioOutputs.First(a => $"audio_{a.CodecToken}" == audioGroupId);

            string? videoCodecTag = GetVideoCodecTag(video);
            string? audioCodecTag = primaryAudio is null
                ? null
                : GetAudioCodecTag(primaryAudio, multipleAudioGroups);
            string codecsAttr = BuildCodecsAttribute(videoCodecTag, audioCodecTag);

            // Resolve this variant's own playlist path up front — it is the key
            // into videoMetrics (HlsOutputStrategy stored each variant's measured
            // bitrate under the SAME resolved path). Keying by MapLabel instead
            // collapses every variant onto "[v0]" and gives them all one shared
            // BANDWIDTH.
            Dictionary<string, string> tokens = TemplateResolver.VideoTokens(
                video.Width,
                video.Height,
                video.IsHdrOutput
            );
            string playlistResolved = TemplateResolver.Resolve(video.PlaylistNameTemplate, tokens);
            string subDir = StoragePathHelpers.GetParent(playlistResolved) ?? playlistResolved;
            string playlistFile = StoragePathHelpers.GetName(playlistResolved);

            // Use measured bandwidth. Apple requires BANDWIDTH = peak, AVERAGE-BANDWIDTH = average.
            // Combine video + audio bandwidth for the STREAM-INF (Apple spec section 4.10).
            VariantMetrics vidMetrics = videoMetrics.GetValueOrDefault(playlistResolved, new(0, 0));

            // Skip video variants whose segments never materialised — bundle
            // got cancelled / failed / didn't publish. Listing them in the
            // master makes VLC and hls.js bail on first init.mp4 fetch with
            // a 404. The analyzer returns zero bandwidth for a missing
            // playlist or empty segment dir.
            if (vidMetrics.PeakBandwidth == 0)
                continue;

            VariantMetrics audMetrics = new(0, 0);
            if (primaryAudio is not null)
            {
                Dictionary<string, string> audioTokens = TemplateResolver.AudioTokens(
                    primaryAudio.Language ?? "und",
                    primaryAudio.CodecToken,
                    primaryAudio.Channels
                );
                string audioResolved = TemplateResolver.Resolve(
                    primaryAudio.PlaylistNameTemplate,
                    audioTokens
                );
                audMetrics = audioMetrics.GetValueOrDefault(audioResolved, new(0, 0));
            }

            int peakBandwidth = vidMetrics.PeakBandwidth + audMetrics.PeakBandwidth;
            int avgBandwidth = vidMetrics.AverageBandwidth + audMetrics.AverageBandwidth;

            // VIDEO-RANGE labels the colour pipeline (PQ/HLG = HDR transfer,
            // SDR otherwise) — not the bit depth. 10-bit anime / 10-bit BT.709
            // remux is SDR; mislabelling them as PQ makes hls.js reject the
            // master with manifestIncompatibleCodecsError because the segments'
            // actual transfer characteristics don't match.
            string videoRange = video.IsHdrOutput ? "PQ" : "SDR";
            string frameRate = video.FrameRate.ToString("F3", CultureInfo.InvariantCulture);

            string subsAttr =
                activeSubs.Length > 0 ? ",SUBTITLES=\"subs\"" : ",CLOSED-CAPTIONS=NONE";

            // Never point AUDIO="..." at a group with zero EXT-X-MEDIA members —
            // a title with genuinely no audio must emit neither the group nor
            // this reference, not a dangling one a player fails to resolve.
            string audioAttr = audioGroupId is not null
                ? $",AUDIO=\"{audioGroupId}\""
                : string.Empty;

            sb.AppendLine(
                $"#EXT-X-STREAM-INF:BANDWIDTH={peakBandwidth},AVERAGE-BANDWIDTH={avgBandwidth},RESOLUTION={video.Width}x{video.Height},FRAME-RATE={frameRate}{codecsAttr},VIDEO-RANGE={videoRange}{audioAttr}{subsAttr}"
            );
            sb.AppendLine($"{subDir}/{playlistFile}.m3u8");
        }

        // Emit EXT-X-DATERANGE chapter markers (HLS v8).
        // START-DATE is a UTC ISO 8601 instant derived from epoch + chapter offset,
        // so players can render a chapter timeline without decoding position.
        if (plan.Chapters is { Count: > 0 } chapters)
        {
            DateTimeOffset epoch = DateTimeOffset.UnixEpoch;

            for (int i = 0; i < chapters.Count; i++)
            {
                ChapterInfo chapter = chapters[i];
                double startSeconds = chapter.Start.TotalSeconds;
                double endSeconds =
                    i + 1 < chapters.Count
                        ? chapters[i + 1].Start.TotalSeconds
                        : chapter.End.TotalSeconds;
                double durationSeconds = endSeconds - startSeconds;

                DateTimeOffset startDate = epoch.AddSeconds(startSeconds);
                string startDateIso = startDate.ToString(
                    "yyyy-MM-ddTHH:mm:ss.fffZ",
                    CultureInfo.InvariantCulture
                );
                string title = chapter.Title ?? $"Chapter {i + 1}";
                string escapedTitle = title.Replace("\"", "\\\"");
                string durationFormatted = durationSeconds.ToString(
                    "F3",
                    CultureInfo.InvariantCulture
                );

                sb.AppendLine(
                    $"#EXT-X-DATERANGE:ID=\"ch{i}\",START-DATE=\"{startDateIso}\",DURATION={durationFormatted},X-COM-NOMERCY-CHAPTER-TITLE=\"{escapedTitle}\""
                );
            }
        }

        return sb.ToString();
    }

    private static string GetAudioDisplayName(string language) =>
        Culture.EnglishLanguageName(language);

    private static string? GetVideoCodecTag(VideoOutputPlan video) =>
        HlsCodecsStringBuilder.VideoCodecString(
            video.EncoderName,
            video.Profile,
            video.Level,
            video.TenBit,
            video.Width,
            video.Height,
            video.FrameRate,
            video.PixelFormat
        );

    // A copied Dolby/DTS rendition is only tagged when an AAC twin exists, so the
    // tag is what lets a client without Dolby pick the AAC variants.
    private static string? GetAudioCodecTag(AudioOutputPlan audio, bool tagCopiedDolby)
    {
        if (tagCopiedDolby && audio.Action == StreamAction.Copy)
        {
            return audio.CodecToken.ToLowerInvariant() switch
            {
                "ac3" => HlsCodecsStringBuilder.ForAc3(),
                "eac3" => HlsCodecsStringBuilder.ForEac3(),
                "dts" => "dtsc",
                _ => null,
            };
        }

        return HlsCodecsStringBuilder.AudioCodecString(audio.EncoderName);
    }

    /// <summary>
    /// Builds the ",CODECS=&quot;...&quot;" clause (leading comma included) from
    /// whichever of the video/audio tags are known. Returns an empty string —
    /// omitting the whole attribute — when neither is known (e.g. both streams
    /// are copy-mode), which is spec-legal and safer than a partially-guessed value.
    /// </summary>
    private static string BuildCodecsAttribute(string? videoCodec, string? audioCodec)
    {
        List<string> parts = [];
        if (videoCodec is not null)
            parts.Add(videoCodec);
        if (audioCodec is not null)
            parts.Add(audioCodec);

        return parts.Count > 0 ? $",CODECS=\"{string.Join(",", parts)}\"" : string.Empty;
    }

    private static string YesNo(bool value) => value ? "YES" : "NO";

    // ------------------------------------------------------------------
    // Subtitle helpers
    // ------------------------------------------------------------------

    private static string GetSubtitleDisplayName(string language, string variant = "full")
    {
        string langName = Culture.EnglishLanguageName(language);

        return variant.ToLowerInvariant() switch
        {
            "sign" => $"{langName} (Signs & Songs)",
            "sdh" => $"{langName} (SDH)",
            "alt" => $"{langName} (Alt)",
            _ => langName,
        };
    }

    // ASS/SRT sidecars play directly from the file URI; only WebVTT needs
    // a single-segment .m3u8 wrapper because that's what HLS spec requires
    // for EXT-X-MEDIA. All subtitle assets live under subtitles/{lang}/ so
    // a season with many tracks doesn't flatten into one giant directory.
    internal static string GetSubtitlePlaylistUri(SubtitleOutputPlan sub)
    {
        string lang = sub.Language ?? "und";
        string variant = string.IsNullOrEmpty(sub.Variant) ? "full" : sub.Variant;

        return sub.OutputCodec switch
        {
            SubtitleCodecType.Ass => $"subtitles/{lang}/{variant}.ass",
            SubtitleCodecType.Srt => $"subtitles/{lang}/{variant}.srt",
            _ => $"subtitles/{lang}/{variant}.m3u8",
        };
    }

    /// <summary>
    /// Emits the per-subtitle media playlist (.m3u8) referencing the WebVTT
    /// segments produced by <see cref="WebVttSegmenter"/>. Playlist lives at
    /// <c>subtitles/{lang}/{variant}.m3u8</c>; segment URIs are relative to
    /// that folder (<c>{variant}_NNNNN.vtt</c>), so the m3u8 is portable as
    /// long as it stays alongside its segments.
    /// </summary>
    public static string GenerateSubtitleMediaPlaylist(
        SubtitleOutputPlan sub,
        IReadOnlyList<WebVttSegment> segments,
        int segmentDurationSeconds,
        string segmentUriPrefix = ""
    )
    {
        string variant = string.IsNullOrEmpty(sub.Variant) ? "full" : sub.Variant;

        StringBuilder sb = new();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        sb.AppendLine($"#EXT-X-TARGETDURATION:{segmentDurationSeconds}");
        sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");

        foreach (WebVttSegment seg in segments)
        {
            double actualDuration = (seg.EndTime - seg.StartTime).TotalSeconds;
            string segFile = $"{variant}_{seg.Index:D5}.vtt";
            string uri = string.IsNullOrEmpty(segmentUriPrefix)
                ? segFile
                : $"{segmentUriPrefix}/{segFile}";

            sb.AppendLine(
                $"#EXTINF:{actualDuration.ToString("F3", CultureInfo.InvariantCulture)},"
            );
            sb.AppendLine(uri);
        }

        sb.AppendLine("#EXT-X-ENDLIST");
        return sb.ToString();
    }

    /// <summary>
    /// Emits a minimal media playlist for a sidecar .ass file (single entry,
    /// no segmentation). The NoMercy player loads the .ass directly via
    /// libass-wasm; the m3u8 is the HLS hook that advertises it.
    /// </summary>
    public static string GenerateAssMediaPlaylist(
        SubtitleOutputPlan sub,
        string assFileName,
        int segmentDurationSeconds
    )
    {
        string lang = sub.Language ?? "und";
        StringBuilder sb = new();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        sb.AppendLine($"#EXT-X-TARGETDURATION:{segmentDurationSeconds}");
        sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        sb.AppendLine($"#EXTINF:{segmentDurationSeconds}.000,");
        sb.AppendLine(assFileName);
        sb.AppendLine("#EXT-X-ENDLIST");
        return sb.ToString();
    }
}
