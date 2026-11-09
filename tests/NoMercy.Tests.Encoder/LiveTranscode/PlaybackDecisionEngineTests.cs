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
using NoMercy.Encoder.LiveTranscode;

namespace NoMercy.Tests.Encoder.LiveTranscode;

public class PlaybackDecisionEngineTests
{
    private readonly PlaybackDecisionEngine _engine = new();

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static VideoStreamInfo MakeVideo(
        string codec,
        int width = 1920,
        int height = 1080,
        long bitrateKbps = 8000,
        bool hdr = false
    ) =>
        new(
            Index: 0,
            Codec: codec,
            Width: width,
            Height: height,
            FrameRate: 24.0,
            BitDepth: hdr ? 10 : 8,
            PixelFormat: hdr ? "yuv420p10le" : "yuv420p",
            ColorPrimaries: hdr ? "bt2020" : "bt709",
            ColorTransfer: hdr ? "smpte2084" : "bt709",
            ColorSpace: hdr ? "bt2020nc" : "bt709",
            IsDefault: true,
            BitRateKbps: bitrateKbps
        );

    private static AudioStreamInfo MakeAudio(string codec, long bitrateKbps = 192) =>
        new(
            Index: 1,
            Codec: codec,
            Channels: 2,
            SampleRate: 48000,
            BitRateKbps: bitrateKbps,
            Language: "eng",
            IsDefault: true,
            IsForced: false
        );

    private static MediaInfo MakeMedia(
        string format,
        VideoStreamInfo? video = null,
        AudioStreamInfo? audio = null
    )
    {
        List<VideoStreamInfo> videos = video is not null ? [video] : [];
        List<AudioStreamInfo> audios = audio is not null ? [audio] : [];

        return new(
            FilePath: "/media/test.file",
            Format: format,
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 10000,
            FileSizeBytes: 1_000_000_000L,
            VideoStreams: videos,
            AudioStreams: audios,
            SubtitleStreams: [],
            Chapters: []
        );
    }

    private static ClientCapabilities MakeClient(
        VideoCodecType[] videoCodecs,
        AudioCodecType[] audioCodecs,
        string[] containers,
        int maxWidth = 7680,
        int maxHeight = 4320,
        bool supportsHdr = true,
        bool supports10Bit = true,
        int maxBitrateKbps = 0
    ) =>
        new(
            SupportedVideoCodecs: videoCodecs,
            SupportedAudioCodecs: audioCodecs,
            SupportedContainers: containers,
            MaxWidth: maxWidth,
            MaxHeight: maxHeight,
            SupportsHdr: supportsHdr,
            Supports10Bit: supports10Bit,
            MaxBitrateKbps: maxBitrateKbps
        );

    // SDR 10-bit: 10-bit is a decoder trait independent of HDR. NoMercy's own
    // HLS output is frequently SDR 10-bit HEVC.
    private static VideoStreamInfo MakeVideo10BitSdr(string codec) =>
        new(
            Index: 0,
            Codec: codec,
            Width: 1920,
            Height: 1080,
            FrameRate: 24.0,
            BitDepth: 10,
            PixelFormat: "yuv420p10le",
            ColorPrimaries: "bt709",
            ColorTransfer: "bt709",
            ColorSpace: "bt709",
            IsDefault: true,
            BitRateKbps: 8000
        );

    // ──────────────────────────────────────────────────────────────────────────
    // Bit depth
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hevc10BitSdr_ClientHasCodecButNo10Bit_TranscodesVideo()
    {
        // The exact real-world case: browser lists HEVC but not 10-bit, source is
        // SDR 10-bit HEVC HLS. Without the bit-depth gate this was judged "codec
        // compatible" and remuxed through as undecodable 10-bit.
        MediaInfo media = MakeMedia("hls", MakeVideo10BitSdr("hevc"), MakeAudio("aac"));

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H265],
            [AudioCodecType.Aac],
            ["hls"],
            supportsHdr: true,
            supports10Bit: false
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeVideo);
        decision.Reason.Should().Contain("10-bit");
    }

    [Fact]
    public void Hevc10BitSdr_ClientSupports10Bit_DirectPlay()
    {
        MediaInfo media = MakeMedia("hls", MakeVideo10BitSdr("hevc"), MakeAudio("aac"));

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H265],
            [AudioCodecType.Aac],
            ["hls"],
            supportsHdr: true,
            supports10Bit: true
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.DirectPlay);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Direct play
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void H264_Aac_Mp4_ToMp4Client_DirectPlay()
    {
        MediaInfo media = MakeMedia(
            "mov,mp4,m4a,dash,3gp,3g2,mj2",
            MakeVideo("h264"),
            MakeAudio("aac")
        );

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264],
            [AudioCodecType.Aac],
            ["mp4"]
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.DirectPlay);
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public void H264_Aac_InMkv_ToMkvClient_DirectPlay()
    {
        MediaInfo media = MakeMedia("matroska,webm", MakeVideo("h264"), MakeAudio("aac"));

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264],
            [AudioCodecType.Aac],
            ["mkv"]
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.DirectPlay);
        decision.Reason.Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Remux
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void H264_InMkv_ToMp4OnlyClient_Remux()
    {
        MediaInfo media = MakeMedia("matroska,webm", MakeVideo("h264"), MakeAudio("aac"));

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264],
            [AudioCodecType.Aac],
            ["mp4"]
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.Remux);
        decision.Reason.Should().Contain("matroska");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TranscodeAudio
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ac3_Audio_ToAacOnlyClient_TranscodeAudio()
    {
        MediaInfo media = MakeMedia("matroska,webm", MakeVideo("h264"), MakeAudio("ac3"));

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264],
            [AudioCodecType.Aac],
            ["mkv", "mp4"]
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeAudio);
        decision.Reason.Should().Contain("Audio");
    }

    private static MediaInfo MakeMediaWithAudios(
        string format,
        VideoStreamInfo video,
        params AudioStreamInfo[] audios
    ) =>
        new(
            FilePath: "/media/test.file",
            Format: format,
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 10000,
            FileSizeBytes: 1_000_000_000L,
            VideoStreams: [video],
            AudioStreams: [.. audios],
            SubtitleStreams: [],
            Chapters: []
        );

    [Fact]
    public void Eac3ThenAac_ToAacOnlyClient_DirectPlay()
    {
        MediaInfo media = MakeMediaWithAudios(
            "hls,applehttp",
            MakeVideo("h264"),
            MakeAudio("eac3"),
            MakeAudio("aac")
        );

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264],
            [AudioCodecType.Aac],
            ["hls"]
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.DirectPlay);
    }

    [Fact]
    public void Eac3ThenDts_ToAacOnlyClient_TranscodeAudio()
    {
        MediaInfo media = MakeMediaWithAudios(
            "hls,applehttp",
            MakeVideo("h264"),
            MakeAudio("eac3"),
            MakeAudio("dts")
        );

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264],
            [AudioCodecType.Aac],
            ["hls"]
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeAudio);
    }

    [Fact]
    public void AudioOnly_Eac3ThenAac_ToAacOnlyClient_DirectPlay()
    {
        MediaInfo media = MakeMedia("mp4", audio: MakeAudio("eac3")) with
        {
            AudioStreams = [MakeAudio("eac3"), MakeAudio("aac")],
        };

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264],
            [AudioCodecType.Aac],
            ["mp4"]
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.DirectPlay);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TranscodeVideo
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hevc_InMkv_ToH264OnlyClient_TranscodeVideo()
    {
        MediaInfo media = MakeMedia("matroska,webm", MakeVideo("hevc"), MakeAudio("aac"));

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264],
            [AudioCodecType.Aac],
            ["mkv", "mp4"]
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeVideo);
        decision.Reason.Should().Contain("hevc");
    }

    [Fact]
    public void FourK_To1080pMaxClient_TranscodeVideo()
    {
        MediaInfo media = MakeMedia(
            "matroska,webm",
            MakeVideo("h264", width: 3840, height: 2160),
            MakeAudio("aac")
        );

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264],
            [AudioCodecType.Aac],
            ["mkv", "mp4"],
            maxWidth: 1920,
            maxHeight: 1080
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeVideo);
        decision.Reason.Should().Contain("3840x2160");
    }

    [Fact]
    public void Hdr_ToNonHdrClient_TranscodeVideo()
    {
        MediaInfo media = MakeMedia(
            "matroska,webm",
            MakeVideo("hevc", hdr: true),
            MakeAudio("aac")
        );

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264, VideoCodecType.H265],
            [AudioCodecType.Aac],
            ["mkv", "mp4"],
            supportsHdr: false
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeVideo);
        decision.Reason.Should().Contain("HDR");
    }

    [Fact]
    public void BitrateExceedsLimit_TranscodeVideo()
    {
        MediaInfo media = MakeMedia(
            "matroska,webm",
            MakeVideo("h264", bitrateKbps: 40000),
            MakeAudio("aac")
        );

        ClientCapabilities client = MakeClient(
            [VideoCodecType.H264],
            [AudioCodecType.Aac],
            ["mkv", "mp4"],
            maxBitrateKbps: 8000
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeVideo);
        decision.Reason.Should().Contain("Bitrate");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Audio-only
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AudioOnly_SupportedCodec_DirectPlay()
    {
        MediaInfo media = MakeMedia("flac", video: null, audio: MakeAudio("flac"));

        ClientCapabilities client = MakeClient([], [AudioCodecType.Flac], ["flac"]);

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.DirectPlay);
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public void AudioOnly_UnsupportedCodec_TranscodeAudio()
    {
        MediaInfo media = MakeMedia("flac", video: null, audio: MakeAudio("flac"));

        ClientCapabilities client = MakeClient([], [AudioCodecType.Aac], ["mp4"]);

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeAudio);
        decision.Reason.Should().Contain("Audio codec");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Per-codec capability (Samsung shape): HEVC Main10 present, AVC High10 absent
    // ──────────────────────────────────────────────────────────────────────────

    private static ClientCapabilities MakeSamsungClient() =>
        new(
            Video:
            [
                new VideoCodecCapability(
                    VideoCodecType.H264,
                    ["high", "main"],
                    8,
                    3840,
                    2160,
                    60,
                    [],
                    0
                ),
                new VideoCodecCapability(
                    VideoCodecType.H265,
                    ["main", "main10"],
                    10,
                    3840,
                    2160,
                    60,
                    ["hdr10", "hlg"],
                    0
                ),
            ],
            Audio:
            [
                new AudioCodecCapability(AudioCodecType.Aac, 2, false, true),
                new AudioCodecCapability(AudioCodecType.Ac3, 6, true, false),
            ],
            SupportedContainers: ["mp4", "mkv", "ts"],
            SupportsHdr: true,
            MaxBitrateKbps: 0
        );

    [Fact]
    public void Samsung_Hevc10BitSource_ClientHasHevcMain10_DirectPlay()
    {
        MediaInfo media = MakeMedia("mpegts", MakeVideo10BitSdr("hevc"), MakeAudio("aac"));

        PlaybackDecision decision = _engine.Decide(media, MakeSamsungClient());

        decision.Action.Should().Be(PlaybackAction.DirectPlay);
    }

    [Fact]
    public void Samsung_Avc10BitSource_ClientLacksAvcHigh10_TranscodesVideo()
    {
        // AVC (h264) capability entry tops out at 8-bit — no high10 profile
        // claimed. A 10-bit AVC source must not pass on the strength of the
        // HEVC entry.
        MediaInfo media = MakeMedia("mpegts", MakeVideo10BitSdr("h264"), MakeAudio("aac"));

        PlaybackDecision decision = _engine.Decide(media, MakeSamsungClient());

        decision.Action.Should().Be(PlaybackAction.TranscodeVideo);
        decision.Reason.Should().Contain("8-bit");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Legacy-payload synthesis fallback
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LegacyFlatPayload_NoVideoArray_SynthesizesTodaysBehavior_DirectPlay()
    {
        MediaInfo media = MakeMedia("hls", MakeVideo10BitSdr("hevc"), MakeAudio("aac"));

        ClientCapabilities legacyClient = new(
            Video: null,
            Audio: null,
            SupportedContainers: ["hls"],
            SupportsHdr: true,
            MaxBitrateKbps: 0,
            SupportedVideoCodecs: [VideoCodecType.H265],
            SupportedAudioCodecs: [AudioCodecType.Aac],
            Supports10Bit: true
        );

        PlaybackDecision decision = _engine.Decide(media, legacyClient);

        decision.Action.Should().Be(PlaybackAction.DirectPlay);
    }

    [Fact]
    public void LegacyFlatPayload_NoVideoArray_Supports10BitFalse_TranscodesExactlyLikeToday()
    {
        MediaInfo media = MakeMedia("hls", MakeVideo10BitSdr("hevc"), MakeAudio("aac"));

        ClientCapabilities legacyClient = new(
            Video: null,
            Audio: null,
            SupportedContainers: ["hls"],
            SupportsHdr: true,
            MaxBitrateKbps: 0,
            SupportedVideoCodecs: [VideoCodecType.H265],
            SupportedAudioCodecs: [AudioCodecType.Aac],
            Supports10Bit: false
        );

        PlaybackDecision decision = _engine.Decide(media, legacyClient);

        decision.Action.Should().Be(PlaybackAction.TranscodeVideo);
        decision.Reason.Should().Contain("10-bit");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Audio passthrough / channel gating
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Audio_ChannelsExceedCapability_TranscodeAudio()
    {
        AudioStreamInfo sevenOne = new(
            Index: 1,
            Codec: "ac3",
            Channels: 8,
            SampleRate: 48000,
            BitRateKbps: 640,
            Language: "eng",
            IsDefault: true,
            IsForced: false
        );

        MediaInfo media = MakeMedia("matroska,webm", MakeVideo("h264"), sevenOne);

        ClientCapabilities client = new(
            Video: [new VideoCodecCapability(VideoCodecType.H264, [], 8, 3840, 2160, 60, [], 0)],
            Audio: [new AudioCodecCapability(AudioCodecType.Ac3, 6, true, false)],
            SupportedContainers: ["mkv"],
            SupportsHdr: false,
            MaxBitrateKbps: 0
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeAudio);
    }

    [Fact]
    public void Audio_NeitherPassthroughNorDecode_TranscodeAudio()
    {
        MediaInfo media = MakeMedia("matroska,webm", MakeVideo("h264"), MakeAudio("ac3"));

        ClientCapabilities client = new(
            Video: [new VideoCodecCapability(VideoCodecType.H264, [], 8, 3840, 2160, 60, [], 0)],
            Audio: [new AudioCodecCapability(AudioCodecType.Ac3, 6, false, false)],
            SupportedContainers: ["mkv"],
            SupportsHdr: false,
            MaxBitrateKbps: 0
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeAudio);
    }

    [Fact]
    public void Audio_PassthroughOnly_NoDecode_DirectPlay()
    {
        MediaInfo media = MakeMedia("matroska,webm", MakeVideo("h264"), MakeAudio("ac3"));

        ClientCapabilities client = new(
            Video: [new VideoCodecCapability(VideoCodecType.H264, [], 8, 3840, 2160, 60, [], 0)],
            Audio: [new AudioCodecCapability(AudioCodecType.Ac3, 6, true, false)],
            SupportedContainers: ["mkv"],
            SupportsHdr: false,
            MaxBitrateKbps: 0
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.DirectPlay);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Legacy flat shape vs per-codec shape — the exact bug reported live:
    // a 10-bit SDR HEVC anime episode direct-played on a phone whose HEVC
    // decoder cannot open 10-bit, because a flat Supports10Bit=true (true
    // ONLY for some OTHER codec on that device) got applied to every codec
    // ResolveVideo synthesized. Not HDR — real anime HEVC encodes are
    // routinely 10-bit SDR (ColorTransfer bt709, not smpte2084/arib-std-b67).
    // ──────────────────────────────────────────────────────────────────────────

    private static VideoStreamInfo Make10BitSdrHevc() =>
        new(
            Index: 0,
            Codec: "hevc",
            Width: 1920,
            Height: 1080,
            FrameRate: 24.0,
            BitDepth: 10,
            PixelFormat: "yuv420p10le",
            ColorPrimaries: "bt709",
            ColorTransfer: "bt709",
            ColorSpace: "bt709",
            IsDefault: true,
            BitRateKbps: 8000
        );

    [Fact]
    public void TenBitSdrHevc_LegacyFlatSupports10BitAppliesToEveryCodec_WronglyDirectPlays()
    {
        // The exact malformed payload the app sent before the per-codec fix:
        // the device decodes SOME 10-bit codec (say AV1), so the flat
        // Supports10Bit legacy field is true device-wide, and
        // ResolveVideo stamps that one boolean onto H264 AND H265 alike.
        MediaInfo media = MakeMedia("matroska,webm", Make10BitSdrHevc(), MakeAudio("aac"));

        ClientCapabilities client = new(
            SupportedContainers: ["mkv"],
            SupportsHdr: false,
            MaxBitrateKbps: 0,
            SupportedVideoCodecs: [VideoCodecType.H264, VideoCodecType.H265],
            SupportedAudioCodecs: [AudioCodecType.Aac],
            MaxWidth: 3840,
            MaxHeight: 2160,
            Supports10Bit: true
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        // This IS the bug, reproduced against the real engine: a legacy
        // client claiming device-wide 10-bit support gets a direct-play
        // verdict for a codec whose hardware decoder cannot open 10-bit.
        decision.Action.Should().Be(PlaybackAction.DirectPlay);
    }

    [Fact]
    public void TenBitSdrHevc_PerCodecShapeNamesHevcAs8BitOnly_CorrectlyTranscodes()
    {
        // The fixed app payload: HEVC reported at its own real ceiling
        // (8-bit only), a DIFFERENT codec (AV1, absent here) is the one
        // that actually decodes 10-bit. Per-codec keeps them separate.
        MediaInfo media = MakeMedia("matroska,webm", Make10BitSdrHevc(), MakeAudio("aac"));

        ClientCapabilities client = new(
            Video:
            [
                new VideoCodecCapability(VideoCodecType.H264, [], 8, 3840, 2160, 60, [], 0),
                new VideoCodecCapability(VideoCodecType.H265, [], 8, 3840, 2160, 60, [], 0),
            ],
            Audio: [new AudioCodecCapability(AudioCodecType.Aac, 2, false, true)],
            SupportedContainers: ["mkv"],
            SupportsHdr: false,
            MaxBitrateKbps: 0
        );

        PlaybackDecision decision = _engine.Decide(media, client);

        decision.Action.Should().Be(PlaybackAction.TranscodeVideo);
    }
}
