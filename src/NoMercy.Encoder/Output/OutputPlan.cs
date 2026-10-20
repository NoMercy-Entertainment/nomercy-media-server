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

using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Naming;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.PostProcess;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Subtitles;
using DrmConfig = NoMercy.Encoder.BuildingBlocks.Drm.DrmConfig;

namespace NoMercy.Encoder.Output;

public record OutputPlan(
    OutputFormat Format,
    VideoOutputPlan[] VideoOutputs,
    AudioOutputPlan[] AudioOutputs,
    SubtitleOutputPlan[] SubtitleOutputs,
    ThumbnailOutputPlan? Thumbnails,
    int SegmentDurationSeconds = 6,
    // Set by PlanStage when the source carries Dolby Vision metadata AND the
    // video output is stream-copy. Output strategies use this to emit the
    // container-specific codec tag that preserves DV playback signaling
    // (dvh1 for MP4/HLS, hvc1+dv passthrough for MKV).
    bool PreserveDolbyVision = false,
    // DRM config propagated from the profile. BuildStage runs the matching
    // IDrmProcessor to generate key + keyinfo artifacts, then injects the
    // resulting `-hls_key_info_file` path into each video output's extra
    // flags so ffmpeg encrypts segments inline and emits EXT-X-KEY tags
    // in the playlist automatically.
    DrmConfig? Drm = null,
    // HLS muxer options forwarded from the profile. Controls playlist type,
    // segment container format, and independent-segments signaling.
    HlsPlanOptions? HlsOptions = null,
    // Chapter metadata from the source file. Output strategies use this to
    // embed chapter data in container-appropriate format:
    //   MKV  — stream-copied by FFmpeg automatically; no extra args needed.
    //   MP4  — written as ffmetadata file, injected via -i chapters.ffmeta.
    //   DASH — post-processed into <EventStream> entries in the MPD.
    //   HLS  — emitted as #EXT-X-DATERANGE tags in the master playlist.
    IReadOnlyList<ChapterInfo>? Chapters = null,
    // Resolved by PlanStage when EncodingContext.MediaItem is set.
    // Provides the canonical bundle directory + per-output filenames.
    BundleLayout? Layout = null,
    // Subtitles downloaded by SubtitleAcquisitionService during PlanStage.
    // BuildStage adds exact-match entries as FFmpeg -i inputs.
    IReadOnlyList<AcquiredSubtitle>? AcquiredSubtitles = null,
    // Opt-in: when true the decomposer emits one Chapters task per chapter
    // and BuildStage extracts a single still at the chapter's exact timestamp.
    bool GenerateChapterThumbs = false,
    // When true, HlsOutputStrategy slices each extracted WebVTT subtitle into
    // HLS segments + per-track media playlist (subtitles/{lang}/{variant}.m3u8)
    // and the master playlist references those m3u8 wrappers. When false the
    // raw .vtt extract still lands on disk for download but no segments are
    // produced and the master playlist omits the EXT-X-MEDIA subtitle entry.
    bool EmitSubtitleWebVttChunks = true,
    // Set by PlanStage when the plan is GPU-resident-eligible and a GPU vendor +
    // scaler are available. BuildStage then decodes with -hwaccel into GPU memory
    // and scales on the GPU (scale_cuda / scale_qsv) so decode + scaling leave
    // the CPU. Null = the CPU filter graph (default, unchanged behaviour).
    GpuAccelPlan? GpuAccel = null,
    // Profile-level CustomArguments — the global escape hatch. BuildStage emits
    // these as ffmpeg global options (before the -i input) so a profile or plugin
    // can pass whole-command flags the schema doesn't model. Per-stream overrides
    // live on each VideoOutputPlan/AudioOutputPlan/SubtitleOutputPlan.ExtraFlags.
    Dictionary<string, string>? GlobalExtraFlags = null,
    // Set by PlanStage when the source is variable-frame-rate. Segmented output
    // strategies (HLS/DASH) then force constant-frame-rate muxing (-fps_mode cfr)
    // so segment durations stay aligned to the target — VFR PTS gaps otherwise
    // drift the segment boundaries and desync playback across an ABR switch.
    bool NormalizeToConstantFrameRate = false
);

public record VideoOutputPlan(
    int Width,
    int Height,
    string EncoderName,
    int Crf,
    int BitrateKbps,
    string? Preset,
    string? Profile,
    string? Level,
    bool TenBit,
    string PixelFormat,
    string MapLabel,
    Dictionary<string, string> ExtraFlags,
    double FrameRate = 23.976,
    string SegmentNameTemplate = ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
    string PlaylistNameTemplate = ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
    bool ConvertHdrToSdr = false,
    string? TonemapFilterChain = null,
    string? CropFilter = null,
    // True when this output preserves the source's HDR transfer characteristics
    // (PQ / HLG / SMPTE2084). Used by the :colorrange: template token so folder
    // and playlist names label outputs HDR vs SDR based on the actual color
    // pipeline, not bit depth — 10-bit BT.709 is SDR and conflating depth with
    // HDR was mislabeling every 10-bit anime / SDR remux as HDR.
    bool IsHdrOutput = false,
    // The ffprobe stream index of the source video stream this output was
    // built from — set by PlanStage at the point the source stream is in
    // hand. -1 (default) means unset, kept only so existing positional
    // constructions elsewhere in the codebase keep compiling. The
    // reconstruction blueprint joins tracks back to source.ffprobe.streams[]
    // by this index rather than re-deriving it by array position later — see
    // .claude/specs/reconstruction-blueprint/SPEC.md "The match key must be
    // captured, not guessed".
    int SourceStreamIndex = -1,
    // ffprobe color_transfer of an HDR output: arib-std-b67 writes VIDEO-RANGE=HLG.
    // Null on an HDR output means PQ.
    string? ColorTransfer = null
);

public record AudioOutputPlan(
    string EncoderName,
    int BitrateKbps,
    int Channels,
    int SampleRate,
    StreamAction Action,
    string? Language,
    string MapLabel,
    string SegmentNameTemplate = ":type:_:language:_:codec:/:type:_:language:_:codec:",
    string PlaylistNameTemplate = ":type:_:language:_:codec:/:type:_:language:_:codec:",
    string? AudioFilter = null,
    // Per-audio CustomArguments escape hatch, merged by output strategies into the
    // OutputOptions for this stream. Empty (default) = no extra flags.
    Dictionary<string, string>? ExtraFlags = null,
    // The real ffprobe codec_name of the matched source stream (e.g. "opus",
    // "aac"), independent of EncoderName. Only meaningful for Action == Copy —
    // EncoderName resolves to the literal "copy" pseudo-encoder for a copied
    // stream, so CodecToken below falls back to this field to name the
    // on-disk rendition after what the bytes actually are.
    string? SourceCodecName = null,
    // The ffprobe stream index of the source audio stream this output was
    // built from — set by AudioPlanBuilder at the point the source stream is
    // in hand. -1 (default) means unset. See VideoOutputPlan.SourceStreamIndex
    // for why this must be captured rather than re-derived by language match.
    int SourceStreamIndex = -1,
    // The source stream's default disposition. The master playlist marks this
    // rendition DEFAULT=YES so playback follows what the release intended
    // instead of whichever stream ffprobe happened to list first.
    bool IsSourceDefault = false,
    // The source stream's channel count. A copied rendition keeps it; Channels
    // above is the profile target, which a copied stream does not follow.
    int SourceChannels = 0
)
{
    /// <summary>Channels the written rendition has; 0 when unknown (a plan rebuilt from disk).</summary>
    public int OutputChannels => Action == StreamAction.Copy ? SourceChannels : Channels;

    /// <summary>
    /// The value that fills the <c>{codec}</c> / <c>:codec:</c> naming-template
    /// token. For a stream-copied output this must be the real source codec
    /// (e.g. "opus"), never the literal "copy" pseudo-encoder name — two
    /// same-language tracks copied from different source codecs would
    /// otherwise both resolve to "audio_eng_copy" and collide on disk.
    /// </summary>
    public string CodecToken =>
        Action == StreamAction.Copy && !string.IsNullOrEmpty(SourceCodecName)
            ? SourceCodecName
            : EncoderName.Replace("libfdk_", "").Replace("lib", "");
}

public record SubtitleOutputPlan(
    SubtitleCodecType OutputCodec,
    StreamAction Action,
    string? Language,
    int SourceIndex,
    string? MapLabel,
    string PlaylistNameTemplate = "subtitles/:filename:.:language:.:variant:",
    SubtitlePolicy Policy = SubtitlePolicy.Extract,
    // Variant slot — full / sign / sdh / alt — derived from the source stream's
    // title and disposition flags so multi-track sources keep distinct URIs.
    string Variant = "full",
    // Per-subtitle CustomArguments escape hatch, merged by output strategies into
    // the OutputOptions for this stream. Empty (default) = no extra flags.
    Dictionary<string, string>? ExtraFlags = null
);

/// <summary>
/// <paramref name="Grid"/> is the tile layout the sheet is pinned to. Stated
/// rather than left to the muxer, because a grid the frames do not fill exactly
/// ends in a block of green — see <see cref="SpriteGrid"/>.
/// </summary>
public record ThumbnailOutputPlan(int Width, int Height, int IntervalSeconds, SpriteGrid Grid);
