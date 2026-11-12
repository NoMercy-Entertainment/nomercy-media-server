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
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Encoder.LiveTranscode;

/// <summary>
/// Builds the ffmpeg argv for one live-transcode HLS session. A pure function
/// of <see cref="LiveRunInput"/> — reuses the VOD command-building layer's
/// building blocks (<see cref="EncoderArgumentResolver"/> for preset
/// validation, <see cref="ProfileRuleValidator.ReservedFlags"/> for the
/// CustomArguments escape hatch) instead of re-deriving encoder constants.
/// </summary>
internal static class LiveFfmpegArgumentBuilder
{
    // Low-latency preset per encoder family. VOD's PresetResolver defaults to
    // the encoder's MIDDLE preset (quality/speed balance for an offline job);
    // live transcoding must keep up with real-time playback, so it needs a
    // fast preset even at the cost of some compression efficiency.
    private static readonly Dictionary<string, string> LivePresetByEncoder = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["libx264"] = "veryfast",
        ["libx265"] = "veryfast",
        ["h264_nvenc"] = "p4",
        ["hevc_nvenc"] = "p4",
        ["h264_amf"] = "speed",
        ["hevc_amf"] = "speed",
        ["h264_qsv"] = "veryfast",
        ["hevc_qsv"] = "veryfast",
    };

    private static readonly CodecRegistry Registry = new();

    public static string[] Build(LiveRunInput input)
    {
        string playlist = Path.Combine(input.OutputDirectory, LiveFfmpegRunner.PlaylistFileName);
        string segmentPattern = Path.Combine(
            input.OutputDirectory,
            $"{LiveFfmpegRunner.SegmentPrefix}%05d.ts"
        );

        List<string> args = ["-hide_banner", "-nostats", "-loglevel", "error"];

        // Input options (e.g. the "-headers" auth line for an HTTP self-ingest URL)
        // must precede "-i" to bind to the input that follows.
        if (input.ExtraInputArgs is { Length: > 0 })
            args.AddRange(input.ExtraInputArgs);

        // Absolute segment indexing. The playlist the client sees lists every
        // segment for the whole runtime up front, so segment N must always mean
        // source time [N*segDur, (N+1)*segDur) no matter which position this
        // runner was spawned at. Snap the input seek to the segment boundary and
        // number the muxer's output from that same index (see AppendHls's
        // -start_number) so a runner spawned by a seek produces seg_N that maps
        // 1:1 to the index hls.js requests. Any sub-segment offset the user
        // actually seeked to is resolved by hls.js seeking within the segment.
        int startIndex = StartSegmentIndex(input);
        if (startIndex > 0)
        {
            args.Add("-ss");
            args.Add(FormatSeconds((double)startIndex * input.SegmentDurationSeconds));
        }

        args.Add("-i");
        args.Add(input.InputPath);

        // An audio-only rendition run drops video entirely and transcodes just the
        // one selected language to AAC. It shares the seek / absolute-index / HLS
        // scaffolding below with the video path (a language rendition must seek to
        // the same segment boundaries the video does), only the codec mapping
        // differs — so it branches here and skips the whole video block.
        if (input.AudioRenditionOnly)
        {
            args.Add("-vn");
            AppendAudioRendition(args, input);
        }
        else
        {
            bool hasVideo = input.SourceInfo is null || input.SourceInfo.HasVideo;
            PlaybackAction action = DetermineAction(input, hasVideo);

            if (hasVideo)
                AppendVideo(args, input, action);
            else
                args.Add("-vn");

            // A source that ships its own browser-ready HLS audio renditions is
            // transcoded video-only; the master playlist points the player at those
            // renditions, so muxing audio here would be wasted work.
            if (input.VideoOnly)
                args.Add("-an");
            else
                AppendAudio(args, input, action);
        }

        // Stop where already-transcoded content resumes (see LiveGapPlanner).
        // "-t" is measured on the timeline BEFORE "-output_ts_offset" below, so it
        // counts from the "-ss" seek position, not from the shifted output PTS —
        // which is why the bound is expressed as a duration from this run's start
        // rather than as an absolute "-to" against the offset output clock.
        if (input.StopPosition is { } stopPosition)
        {
            double startSeconds = (double)startIndex * input.SegmentDurationSeconds;
            double durationSeconds = stopPosition.TotalSeconds - startSeconds;

            if (durationSeconds > 0)
            {
                args.Add("-t");
                args.Add(FormatSeconds(durationSeconds));
            }
        }

        // Absolute output timestamps. A runner spawned by a seek uses "-ss" before
        // the input, which resets output PTS to ~0. hls.js then has to reconcile a
        // segment whose PTS starts at 0 against a playlist that places it deep in
        // the timeline, and across the implicit discontinuity (no EXT-X-DISCONTINUITY
        // in a whole-runtime VOD playlist) it mis-positions the fragment and never
        // appends it — the seek "loads forever". Shifting the muxed PTS back to the
        // segment's true start makes segment N carry PTS ≈ N×segDur, matching the
        // playlist exactly, so no client-side remapping is needed. Zero for the
        // start-at-zero runner, so the common case is unchanged.
        if (startIndex > 0)
        {
            args.Add("-output_ts_offset");
            args.Add(FormatSeconds((double)startIndex * input.SegmentDurationSeconds));
        }

        AppendHls(args, input, segmentPattern, startIndex);

        if (input.CustomArguments is { Count: > 0 })
            AppendCustomArguments(args, input.CustomArguments);

        args.Add(playlist);

        args.Add("-progress");
        args.Add("pipe:1");

        return [.. args];
    }

    /// <summary>
    /// Video-less sources always need an audio transcode (DirectPlay never
    /// reaches the live encoder, so a video-less source landing here is by
    /// definition audio-incompatible). When either the client capabilities or
    /// the source analysis is missing, there isn't enough context to evaluate
    /// remux/copy eligibility — default to a full video transcode, the same
    /// behaviour this runner used unconditionally before this branch existed.
    /// </summary>
    private static PlaybackAction DetermineAction(LiveRunInput input, bool hasVideo)
    {
        if (!hasVideo)
            return PlaybackAction.TranscodeAudio;

        if (input.SourceInfo is null || input.Client is null)
            return PlaybackAction.TranscodeVideo;

        PlaybackDecision decision = new PlaybackDecisionEngine().Decide(
            input.SourceInfo,
            input.Client
        );

        // DirectPlay never reaches the live encoder in practice — LiveTranscodeService
        // short-circuits it before a session starts. Treat it defensively as a full
        // transcode rather than emitting a stream copy for a decision this layer
        // never expects to see.
        return decision.Action == PlaybackAction.DirectPlay
            ? PlaybackAction.TranscodeVideo
            : decision.Action;
    }

    // True only when the argv re-encodes video; copy runs need no GPU slot.
    internal static bool EncodesVideo(LiveRunInput input)
    {
        if (input.AudioRenditionOnly)
            return false;

        bool hasVideo = input.SourceInfo is null || input.SourceInfo.HasVideo;
        if (!hasVideo)
            return false;

        return DetermineAction(input, hasVideo)
            is not (PlaybackAction.Remux or PlaybackAction.TranscodeAudio);
    }

    private static bool SelectedAudioPlayable(LiveRunInput input)
    {
        IReadOnlyList<AudioStreamInfo>? streams = input.SourceInfo?.AudioStreams;
        if (streams is null || input.Client is null)
            return false;

        return input.AudioStreamIndex >= 0
            && input.AudioStreamIndex < streams.Count
            && PlaybackDecisionEngine.IsAudioPlayable(
                streams[input.AudioStreamIndex],
                input.Client
            );
    }

    private static void AppendVideo(List<string> args, LiveRunInput input, PlaybackAction action)
    {
        args.Add("-map");
        args.Add("0:v:0");

        // Remux and TranscodeAudio both mean the video codec is already
        // client-compatible — only the container or the audio track needs to
        // change, so re-encoding video would burn CPU for zero benefit. This is
        // the large CPU saving for the common "H.264-in-MKV, browser wants
        // fMP4/TS" case.
        if (action is PlaybackAction.Remux or PlaybackAction.TranscodeAudio)
        {
            args.Add("-c:v");
            args.Add("copy");
            return;
        }

        LiveQuality quality = input.Quality;

        args.Add("-c:v");
        args.Add(quality.Encoder);

        args.Add("-b:v");
        args.Add($"{quality.BitrateKbps}k");

        string? preset = ResolveLivePreset(quality.Encoder);
        if (preset is not null)
        {
            args.Add("-preset");
            args.Add(preset);
        }

        // VBV cap around the ABR target so segment sizes stay predictable for
        // HLS clients instead of ballooning on complex scenes.
        int maxrateKbps = (int)Math.Round(quality.BitrateKbps * 1.5);
        int bufsizeKbps = quality.BitrateKbps * 2;
        args.Add("-maxrate");
        args.Add($"{maxrateKbps}k");
        args.Add("-bufsize");
        args.Add($"{bufsizeKbps}k");

        args.Add("-vf");
        args.Add(BuildVideoFilterChain(input));

        AppendGop(args, input);
    }

    /// <summary>
    /// Resolves a low-latency preset for the encoder, validated against the
    /// encoder's actual preset vocabulary via the same
    /// <see cref="EncoderArgumentResolver"/> the VOD pipeline uses. Returns
    /// null for encoders with no preset concept (VAAPI, VideoToolbox) or an
    /// unrecognised encoder name.
    /// </summary>
    private static string? ResolveLivePreset(string encoderName)
    {
        string? requested = LivePresetByEncoder.GetValueOrDefault(encoderName);
        EncoderInfo? encoderInfo = Registry.GetVideoEncoderByName(encoderName);

        return encoderInfo is not null
            ? EncoderArgumentResolver.ResolvePreset(requested, encoderInfo)
            : requested;
    }

    private static string BuildVideoFilterChain(LiveRunInput input)
    {
        bool needsTonemap =
            input.Client is { SupportsHdr: false } && (input.SourceInfo?.IsHdr ?? false);

        string vfChain = $"scale={input.Quality.Width}:{input.Quality.Height}";

        // Browser-safe 8-bit: a browser's software/H.264 decode path cannot
        // play High-10/Main10. Force yuv420p on EVERY transcoded output, not
        // only the HDR→SDR tonemap chain — without this, a 10-bit HEVC SDR
        // source silently encodes as High-10 and simply fails to decode.
        vfChain += needsTonemap
            ? ",zscale=t=linear:npl=100,format=gbrpf32le"
                + ",zscale=p=bt709"
                + ",tonemap=tonemap=hable:desat=0"
                + ",zscale=t=bt709:m=bt709:r=tv"
                + ",format=yuv420p"
            : ",format=yuv420p";

        return vfChain;
    }

    /// <summary>
    /// Aligns keyframes to HLS segment boundaries so every segment is
    /// independently decodable. <c>-force_key_frames</c> guarantees an IDR at
    /// each segment start regardless of <c>-g</c>; <c>-g</c> mirrors the VOD
    /// HLS strategy's ceiling (2× the segment's frame count) as a backstop so
    /// the encoder's own GOP structure never drifts past two segments without
    /// a keyframe.
    /// </summary>
    private static void AppendGop(List<string> args, LiveRunInput input)
    {
        double frameRate =
            input.SourceInfo?.VideoStreams.Count > 0
                ? input.SourceInfo.VideoStreams[0].FrameRate
                : 24;
        int segmentDuration = input.SegmentDurationSeconds;

        args.Add("-force_key_frames");
        args.Add($"expr:gte(t,n_forced*{segmentDuration})");
        args.Add("-forced-idr");
        args.Add("1");

        int gopCeiling = (int)Math.Ceiling(frameRate * segmentDuration * 2);
        args.Add("-g");
        args.Add(gopCeiling.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendAudio(List<string> args, LiveRunInput input, PlaybackAction action)
    {
        // Map the viewer's chosen audio track (resolved from the library language
        // preference, or switched at runtime). Trailing '?' keeps a video-only
        // source from failing when the index has no matching stream.
        args.Add("-map");
        args.Add($"0:a:{input.AudioStreamIndex.ToString(CultureInfo.InvariantCulture)}?");

        // The decision accepts any playable audio stream, so the selected one
        // may still be a codec the client cannot decode: copy only a playable one.
        if (action == PlaybackAction.Remux && SelectedAudioPlayable(input))
        {
            args.Add("-c:a");
            args.Add("copy");
            return;
        }

        // Clamp audio channel count to what the client supports. Read the channel
        // count off the SELECTED stream (a 5.1 English track next to a stereo
        // commentary must downmix from its own layout, not the first stream's).
        int sourceChannels = SelectedAudioChannels(input);
        int clientMax = input.Client?.MaxAudioChannels is > 0 ? input.Client.MaxAudioChannels : 2;
        int outputChannels = Math.Min(sourceChannels, clientMax);

        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add("128k");
        args.Add("-ac");
        args.Add(outputChannels.ToString(CultureInfo.InvariantCulture));
    }

    // Audio-only rendition for one source language. Always transcodes to AAC (the
    // reason this path exists is a source whose audio a browser can't play), with
    // the channel count clamped to what the client supports — a 7.1 or Atmos bed
    // folds down to the client's cap (stereo by default) since browsers can't
    // render object audio and rarely decode 7.1 anyway.
    private static void AppendAudioRendition(List<string> args, LiveRunInput input)
    {
        args.Add("-map");
        args.Add($"0:a:{input.AudioStreamIndex.ToString(CultureInfo.InvariantCulture)}?");

        int sourceChannels = SelectedAudioChannels(input);
        int clientMax = input.Client?.MaxAudioChannels is > 0 ? input.Client.MaxAudioChannels : 2;
        int outputChannels = Math.Min(sourceChannels, clientMax);

        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add("128k");
        args.Add("-ac");
        args.Add(outputChannels.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendHls(
        List<string> args,
        LiveRunInput input,
        string segmentPattern,
        int startIndex
    )
    {
        args.Add("-f");
        args.Add("hls");
        args.Add("-hls_time");
        args.Add(input.SegmentDurationSeconds.ToString(CultureInfo.InvariantCulture));
        args.Add("-hls_list_size");
        args.Add("0");
        // Number the first output segment with its absolute index so a runner
        // spawned at a seek position writes seg_{startIndex}.ts onward, matching
        // the whole-runtime playlist the client already holds.
        args.Add("-start_number");
        args.Add(startIndex.ToString(CultureInfo.InvariantCulture));
        args.Add("-hls_playlist_type");
        args.Add("event");
        args.Add("-hls_flags");
        args.Add("independent_segments+temp_file");
        args.Add("-hls_segment_filename");
        args.Add(segmentPattern);
    }

    /// <summary>
    /// Absolute HLS segment index the first output segment of this run carries.
    /// Floors the (boundary-aligned) start position to whole segments so
    /// segment N always spans source time [N*segDur, (N+1)*segDur) regardless of
    /// which position the runner was spawned at.
    /// </summary>
    // Channel count of the audio stream being mapped, falling back to stereo when
    // the source analysis is missing or the index is out of range.
    private static int SelectedAudioChannels(LiveRunInput input)
    {
        IReadOnlyList<AudioStreamInfo>? streams = input.SourceInfo?.AudioStreams;
        if (streams is null || streams.Count == 0)
            return 2;

        int index =
            input.AudioStreamIndex >= 0 && input.AudioStreamIndex < streams.Count
                ? input.AudioStreamIndex
                : 0;

        return streams[index].Channels;
    }

    private static int StartSegmentIndex(LiveRunInput input)
    {
        if (input.SegmentDurationSeconds <= 0 || input.StartPosition <= TimeSpan.Zero)
            return 0;

        return (int)(input.StartPosition.TotalSeconds / input.SegmentDurationSeconds);
    }

    // Profile/plugin CustomArguments escape hatch — merged last so author
    // intent wins, mirroring PlanStage's per-video merge. Any key already
    // owned by a typed field above is skipped so the argv can never emit a
    // duplicate/conflicting flag next to the value this builder computed.
    private static void AppendCustomArguments(
        List<string> args,
        IReadOnlyDictionary<string, string> customArguments
    )
    {
        foreach ((string key, string value) in customArguments)
        {
            string normalizedKey = key.StartsWith('-') ? key : $"-{key}";
            if (ProfileRuleValidator.ReservedFlags.Contains(normalizedKey))
                continue;
            if (CustomArgumentGuard.ShouldDrop(key, value, true, "live custom arguments"))
                continue;

            args.Add(normalizedKey);
            if (value.Length > 0)
                args.Add(value);
        }
    }

    private static string FormatSeconds(double seconds) =>
        seconds.ToString("F3", CultureInfo.InvariantCulture);
}
