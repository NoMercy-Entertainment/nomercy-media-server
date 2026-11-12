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

namespace NoMercy.Encoder.LiveTranscode;

public class PlaybackDecisionEngine : IPlaybackDecisionEngine
{
    public PlaybackDecision[] DecideBatch(MediaInfo[] library, ClientCapabilities client) =>
        [.. library.Select(m => Decide(m, ResolveVideo(client)))];

    public PlaybackDecision Decide(MediaInfo media, ClientCapabilities client)
    {
        client = ResolveVideo(client);

        // Audio-only path
        if (!media.HasVideo)
        {
            if (media.HasAudio && media.AudioStreams.Any(a => IsAudioCompatible(a, client)))
                return new(PlaybackAction.DirectPlay, null, null);

            return new(PlaybackAction.TranscodeAudio, "Audio codec not supported", null);
        }

        VideoStreamInfo video = media.VideoStreams[0];
        VideoCodecType? sourceCodec = MapVideoCodec(video.Codec);
        VideoCodecCapability? capability = sourceCodec is null
            ? null
            : client.Video.FirstOrDefault(v => v.Codec == sourceCodec.Value);

        bool videoCodecOk = capability is not null;
        bool bitDepthOk = capability is not null && video.BitDepth <= capability.MaxBitDepth;

        // VideoStreamInfo doesn't carry an H.264/HEVC profile string (only
        // DolbyVisionInfo.Profile, a different concept) — there's no source value
        // to gate against yet, so this never rejects on its own. Wired for the day
        // AnalyzeStage adds ffprobe's `profile` field.
        bool profileOk = capability is not null;

        bool resolutionOk =
            capability is not null
            && video.Width <= capability.MaxWidth
            && video.Height <= capability.MaxHeight;
        bool framerateOk = capability is not null && video.FrameRate <= capability.MaxFramerate;
        string? sourceHdrFormat = MapHdrFormat(video);
        bool hdrOk =
            !video.IsHdr
            || (
                capability is not null
                && sourceHdrFormat is not null
                && capability.HdrFormats.Contains(sourceHdrFormat, StringComparer.OrdinalIgnoreCase)
            );
        bool codecBitrateOk =
            capability is null
            || capability.MaxBitrateKbps <= 0
            || video.BitRateKbps <= capability.MaxBitrateKbps;
        bool globalBitrateOk =
            client.MaxBitrateKbps <= 0 || video.BitRateKbps <= client.MaxBitrateKbps;
        bool bitrateOk = codecBitrateOk && globalBitrateOk;

        bool audioCodecOk =
            !media.HasAudio || media.AudioStreams.Any(a => IsAudioCompatible(a, client));
        bool containerOk = IsContainerCompatible(media.Format, client);

        // Video needs transcode — codec, resolution, bit-depth, profile, framerate, or HDR incompatible
        if (!videoCodecOk || !resolutionOk || !hdrOk || !bitDepthOk || !profileOk || !framerateOk)
        {
            string reason;
            if (!videoCodecOk)
                reason = $"Client doesn't support {video.Codec}";
            else if (!resolutionOk)
                reason =
                    $"Resolution {video.Width}x{video.Height} exceeds client's {video.Codec} decoder max {capability!.MaxWidth}x{capability.MaxHeight}";
            else if (!bitDepthOk)
                reason =
                    $"Client's {video.Codec} decoder tops out at {capability!.MaxBitDepth}-bit (source is {video.BitDepth}-bit)";
            else if (!framerateOk)
                reason =
                    $"Framerate {video.FrameRate}fps exceeds client's {video.Codec} decoder max {capability!.MaxFramerate}fps";
            else if (!profileOk)
                reason = $"Client's {video.Codec} decoder doesn't support the source's profile";
            else
                reason =
                    $"Client's {video.Codec} decoder doesn't support HDR format '{sourceHdrFormat ?? "unknown"}'";

            return new(PlaybackAction.TranscodeVideo, reason, null);
        }

        // Video codec OK, but audio needs transcode
        if (!audioCodecOk)
            return new(PlaybackAction.TranscodeAudio, "Audio codec not supported by client", null);

        // Video + audio OK, but container wrong → remux
        if (!containerOk)
            return new(
                PlaybackAction.Remux,
                $"Container '{media.Format}' not supported, remuxing",
                null
            );

        // Everything else OK but bitrate too high → transcode to reduce
        if (!bitrateOk)
            return new(PlaybackAction.TranscodeVideo, "Bitrate exceeds client limit", null);

        return new(PlaybackAction.DirectPlay, null, null);
    }

    /// <summary>
    /// Older client builds send the flat legacy shape (SupportedVideoCodecs/
    /// Supports10Bit/MaxWidth/MaxHeight, no Video/Audio arrays). When client.Video
    /// is empty and a legacy payload is present, synthesize one VideoCodecCapability
    /// per legacy codec, reproducing exactly today's global-boolean behavior — no
    /// profile gate (legacy clients never claimed one) — so Decide() has exactly
    /// one code path.
    /// </summary>
    private static ClientCapabilities ResolveVideo(ClientCapabilities client)
    {
        if (client.Video.Length > 0 || client.SupportedVideoCodecs is null)
            return client;

        int maxBitDepth = client.Supports10Bit == true ? 10 : 8;
        string[] hdrFormats = client.SupportsHdr ? ["hdr10", "hlg"] : [];
        int maxWidth = client.MaxWidth ?? int.MaxValue;
        int maxHeight = client.MaxHeight ?? int.MaxValue;

        VideoCodecCapability[] synthesized =
        [
            .. client.SupportedVideoCodecs.Select(codec => new VideoCodecCapability(
                Codec: codec,
                Profiles: [],
                MaxBitDepth: maxBitDepth,
                MaxWidth: maxWidth,
                MaxHeight: maxHeight,
                MaxFramerate: int.MaxValue,
                HdrFormats: hdrFormats,
                MaxBitrateKbps: 0
            )),
        ];

        AudioCodecCapability[] audio =
            client.Audio.Length > 0 || client.SupportedAudioCodecs is null
                ? client.Audio
                :
                [
                    .. client.SupportedAudioCodecs.Select(codec => new AudioCodecCapability(
                        Codec: codec,
                        MaxChannels: client.MaxAudioChannels,
                        Passthrough: false,
                        Decode: true
                    )),
                ];

        return client with
        {
            Video = synthesized,
            Audio = audio,
        };
    }

    // VideoStreamInfo.IsHdr only tells us the stream is SOME kind of HDR, not
    // which one; Dolby Vision detection lives on a separate DolbyVisionInfo the
    // engine doesn't have here. Derive HDR10 vs HLG from the transfer
    // characteristic ffprobe already reports — the two IsHdr checks against.
    private static string? MapHdrFormat(VideoStreamInfo video)
    {
        if (!video.IsHdr)
            return null;

        return video.ColorTransfer switch
        {
            "smpte2084" => "hdr10",
            "arib-std-b67" => "hlg",
            _ => null,
        };
    }

    internal static bool IsAudioPlayable(AudioStreamInfo audio, ClientCapabilities client) =>
        IsAudioCompatible(audio, ResolveVideo(client));

    private static bool IsAudioCompatible(AudioStreamInfo audio, ClientCapabilities client)
    {
        AudioCodecType? sourceCodec = MapAudioCodec(audio.Codec);
        if (sourceCodec is null)
            return false;

        AudioCodecCapability? capability = client.Audio.FirstOrDefault(a =>
            a.Codec == sourceCodec.Value
        );
        return capability is not null
            && audio.Channels <= capability.MaxChannels
            && (capability.Passthrough || capability.Decode);
    }

    private static bool IsContainerCompatible(string format, ClientCapabilities client)
    {
        string? container = MapContainer(format);
        return container is not null
            && client.SupportedContainers.Any(c =>
                c.Equals(container, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static VideoCodecType? MapVideoCodec(string codec) =>
        codec.ToLowerInvariant() switch
        {
            "h264" => VideoCodecType.H264,
            "hevc" or "h265" => VideoCodecType.H265,
            "av1" => VideoCodecType.Av1,
            "vp9" => VideoCodecType.Vp9,
            _ => null,
        };

    private static AudioCodecType? MapAudioCodec(string codec) =>
        codec.ToLowerInvariant() switch
        {
            "aac" => AudioCodecType.Aac,
            "ac3" => AudioCodecType.Ac3,
            "eac3" => AudioCodecType.Eac3,
            "flac" => AudioCodecType.Flac,
            "opus" => AudioCodecType.Opus,
            "mp3" => AudioCodecType.Mp3,
            "vorbis" => AudioCodecType.Vorbis,
            "truehd" => AudioCodecType.TrueHd,
            "dts" or "dca" => AudioCodecType.Dts,
            _ => null,
        };

    private static string? MapContainer(string format) =>
        format.ToLowerInvariant() switch
        {
            string f when f.Contains("matroska") => "mkv",
            string f when f.Contains("mp4") || f.Contains("mov") => "mp4",
            string f when f.Contains("mpegts") => "ts",
            // ffprobe reports an HLS playlist as "hls,applehttp" (or "applehttp"),
            // never a bare "hls" — an exact match here silently missed every HLS
            // source and forced a needless remux session.
            string f when f.Contains("hls") || f.Contains("applehttp") => "hls",
            string f when f.Contains("flac") => "flac",
            _ => null,
        };
}
