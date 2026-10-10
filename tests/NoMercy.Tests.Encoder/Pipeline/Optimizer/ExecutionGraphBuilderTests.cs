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
using NoMercy.Encoder.Pipeline.Optimizer;
using AudioOutput = NoMercy.Encoder.Profiles.AudioOutput;
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using Container = NoMercy.Encoder.Profiles.Container;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using LadderMode = NoMercy.Encoder.Profiles.LadderMode;
using StreamPolicy = NoMercy.Encoder.Profiles.StreamPolicy;
using SubtitleOutput = NoMercy.Encoder.Profiles.SubtitleOutput;
using SubtitlePolicy = NoMercy.Encoder.Profiles.SubtitlePolicy;
using ThumbnailOutput = NoMercy.Encoder.Profiles.ThumbnailOutput;
using V2RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;
using VideoOutput = NoMercy.Encoder.Profiles.VideoOutput;

namespace NoMercy.Tests.Encoder.Pipeline.Optimizer;

public class ExecutionGraphBuilderTests
{
    private static readonly ExecutionGraphBuilder Builder = new();

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static VideoStreamInfo Sdr1080p =>
        new(
            Index: 0,
            Codec: "h264",
            Width: 1920,
            Height: 1080,
            FrameRate: 24,
            BitDepth: 8,
            PixelFormat: "yuv420p",
            ColorPrimaries: "bt709",
            ColorTransfer: "bt709",
            ColorSpace: "bt709",
            IsDefault: true,
            BitRateKbps: 8000
        );

    private static VideoStreamInfo Hdr4K =>
        new(
            Index: 0,
            Codec: "hevc",
            Width: 3840,
            Height: 2160,
            FrameRate: 24,
            BitDepth: 10,
            PixelFormat: "yuv420p10le",
            ColorPrimaries: "bt2020",
            ColorTransfer: "smpte2084",
            ColorSpace: "bt2020nc",
            IsDefault: true,
            BitRateKbps: 40000
        );

    private static AudioStreamInfo DefaultAudio =>
        new(
            Index: 1,
            Codec: "aac",
            Channels: 2,
            SampleRate: 48000,
            BitRateKbps: 192,
            Language: "eng",
            IsDefault: true,
            IsForced: false
        );

    private static SubtitleStreamInfo EnglishSub =>
        new(Index: 2, Codec: "subrip", Language: "eng", IsDefault: false, IsForced: false);

    private static SubtitleStreamInfo FrenchSub =>
        new(Index: 3, Codec: "subrip", Language: "fra", IsDefault: false, IsForced: false);

    private static ChapterInfo Chapter =>
        new(TimeSpan.Zero, TimeSpan.FromMinutes(90), "Main Feature");

    private static MediaInfo MakeMedia(
        IReadOnlyList<VideoStreamInfo>? video = null,
        IReadOnlyList<AudioStreamInfo>? audio = null,
        IReadOnlyList<SubtitleStreamInfo>? subs = null,
        IReadOnlyList<ChapterInfo>? chapters = null
    ) =>
        new(
            FilePath: "/media/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 10000,
            FileSizeBytes: 8_000_000_000,
            VideoStreams: video ?? [],
            AudioStreams: audio ?? [DefaultAudio],
            SubtitleStreams: subs ?? [],
            Chapters: chapters ?? []
        );

    private static VideoOutput SingleOutput1080pH264 =>
        new(
            Policy: StreamPolicy.Transcode,
            Codec: VideoCodecType.H264,
            Width: 1920,
            Height: 1080,
            RateControl: V2RateControlMode.Crf,
            Crf: 23,
            BitrateKbps: 4000,
            MaxBitrateKbps: null,
            BufferSizeKbps: null,
            Preset: "fast",
            CodecProfile: CodecProfile.High,
            Level: null,
            Tune: null,
            BitDepth: 8,
            PixelFormat: null,
            KeyframeIntervalSeconds: 2,
            ConvertHdrToSdr: false,
            SegmentNameTemplate: "video/{label}",
            PlaylistNameTemplate: "video/{label}/playlist"
        );

    private static AudioOutput DefaultAudioOutput =>
        new(
            Policy: StreamPolicy.Transcode,
            Codec: AudioCodecType.Aac,
            BitrateKbps: 192,
            Channels: 2,
            SampleRateHz: 48000,
            AllowedLanguages: [],
            DefaultLanguage: null,
            Loudness: null,
            Downmix: null,
            SegmentNameTemplate: "audio/{lang}-{codec}",
            PlaylistNameTemplate: "audio/{lang}-{codec}/playlist"
        );

    private static EncodingProfile SingleOutputProfile =>
        new(
            Id: Ulid.NewUlid(),
            Name: "Test",
            Container: Container.HlsTs,
            Video: SingleOutput1080pH264,
            Audio: [DefaultAudioOutput],
            Subtitles: []
        );

    private static EncoderInfo MakeEncoderInfo(string name, bool isHw) =>
        new(
            FfmpegName: name,
            RequiredVendor: isHw ? GpuVendor.Nvidia : null,
            Presets: ["fast", "medium", "slow"],
            Profiles: [],
            Levels: [],
            QualityRange: new(0, 51, 23),
            SupportedRateControl: [RateControlMode.Crf],
            Supports10Bit: false,
            SupportsHdr: false,
            MaxConcurrentSessions: isHw ? 12 : int.MaxValue,
            PixelFormat10Bit: "yuv420p10le",
            VendorSpecificFlags: new()
        );

    private static ResolvedCodec H264Software =>
        new(
            FfmpegEncoderName: "libx264",
            EncoderInfo: MakeEncoderInfo("libx264", false),
            Device: null,
            DefaultRateControl: RateControlMode.Crf
        );

    private static ResolvedCodec H264Nvenc =>
        new(
            FfmpegEncoderName: "h264_nvenc",
            EncoderInfo: MakeEncoderInfo("h264_nvenc", true),
            Device: new(
                GpuVendor.Nvidia,
                "RTX 4090",
                24576,
                12,
                [VideoCodecType.H264, VideoCodecType.H265, VideoCodecType.Av1]
            ),
            DefaultRateControl: RateControlMode.Cq
        );

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public void Simple1080pH264SingleOutput_HasDecodeAndEncodeNodes()
    {
        MediaInfo media = MakeMedia(video: [Sdr1080p], subs: []);
        EncodingProfile profile = SingleOutputProfile;

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, [H264Software]);

        nodes.Should().Contain(n => n.Operation == OperationType.Decode);
        nodes.Should().Contain(n => n.Operation == OperationType.Encode);
        nodes.Should().NotContain(n => n.Operation == OperationType.Tonemap);
        nodes.Should().NotContain(n => n.Operation == OperationType.Split);
    }

    [Fact]
    public void Simple1080pH264SingleOutput_SameResolution_NoScaleNode()
    {
        MediaInfo media = MakeMedia(video: [Sdr1080p], subs: []);
        EncodingProfile profile = SingleOutputProfile;

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, [H264Software]);

        nodes.Should().NotContain(n => n.Operation == OperationType.Scale);
    }

    [Fact]
    public void Simple1080pH264SingleOutput_EncodeNodeHasCorrectParameters()
    {
        MediaInfo media = MakeMedia(video: [Sdr1080p], subs: []);
        EncodingProfile profile = SingleOutputProfile;

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, [H264Software]);

        ExecutionNode encode = nodes.Single(n => n.Operation == OperationType.Encode);
        encode.Parameters["encoder"].Should().Be("libx264");
        encode.Parameters["crf"].Should().Be("23");
        encode.Parameters["preset"].Should().Be("fast");
    }

    [Fact]
    public void Hdr4KMultiResolution_HasDecodeTonemapSplitScaleEncodeChain()
    {
        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "HDR Multi",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H265,
                Width: 1920,
                Height: 1080,
                RateControl: V2RateControlMode.Crf,
                Crf: 22,
                BitrateKbps: 4000,
                MaxBitrateKbps: 6000,
                BufferSizeKbps: 8000,
                Preset: "medium",
                CodecProfile: CodecProfile.Main10,
                Level: null,
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: true,
                SegmentNameTemplate: "video/{label}",
                PlaylistNameTemplate: "video/{label}/playlist"
            ),
            Audio: [DefaultAudioOutput],
            Subtitles: [],
            Ladder: new()
            {
                Mode = LadderMode.Manual,
                Rungs =
                [
                    new(
                        Width: 1920,
                        Height: 1080,
                        Codec: VideoCodecType.H265,
                        BitrateKbps: 4000,
                        MaxBitrateKbps: 6000,
                        BufferSizeKbps: 8000,
                        Framerate: 24.0,
                        Preset: "medium",
                        CodecProfile: CodecProfile.Main10,
                        BitDepth: 8,
                        PixelFormat: null
                    ),
                    new(
                        Width: 1280,
                        Height: 720,
                        Codec: VideoCodecType.H265,
                        BitrateKbps: 2500,
                        MaxBitrateKbps: 4000,
                        BufferSizeKbps: 5000,
                        Framerate: 24.0,
                        Preset: "medium",
                        CodecProfile: CodecProfile.Main10,
                        BitDepth: 8,
                        PixelFormat: null
                    ),
                    new(
                        Width: 854,
                        Height: 480,
                        Codec: VideoCodecType.H265,
                        BitrateKbps: 1200,
                        MaxBitrateKbps: 2000,
                        BufferSizeKbps: 2500,
                        Framerate: 24.0,
                        Preset: "medium",
                        CodecProfile: CodecProfile.Main10,
                        BitDepth: 8,
                        PixelFormat: null
                    ),
                ],
            }
        );

        ResolvedCodec[] resolvedCodecs =
        [
            new("hevc_nvenc", MakeEncoderInfo("hevc_nvenc", true), null, RateControlMode.Cq),
            new("hevc_nvenc", MakeEncoderInfo("hevc_nvenc", true), null, RateControlMode.Cq),
            new("hevc_nvenc", MakeEncoderInfo("hevc_nvenc", true), null, RateControlMode.Cq),
        ];

        MediaInfo media = MakeMedia(video: [Hdr4K]);
        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, resolvedCodecs);

        nodes.Should().Contain(n => n.Operation == OperationType.Decode);
        nodes.Should().Contain(n => n.Operation == OperationType.Tonemap);
        nodes.Should().Contain(n => n.Operation == OperationType.Split);
        nodes.Count(n => n.Operation == OperationType.Scale).Should().Be(3);
        nodes.Count(n => n.Operation == OperationType.Encode).Should().Be(3);
    }

    [Fact]
    public void AudioOnlyInput_HasAudioDecodeAndEncodeOnly()
    {
        MediaInfo media = MakeMedia(video: [], audio: [DefaultAudio]);
        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "Audio Only",
            Container: Container.HlsTs,
            Video: null,
            Audio: [DefaultAudioOutput],
            Subtitles: []
        );

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, []);

        nodes.Should().Contain(n => n.Operation == OperationType.AudioDecode);
        nodes.Should().Contain(n => n.Operation == OperationType.AudioEncode);
        nodes.Should().NotContain(n => n.Operation == OperationType.Decode);
        nodes.Should().NotContain(n => n.Operation == OperationType.Encode);
    }

    [Fact]
    public void MultiSubtitleInput_SubtitleExtractNodesArePresentAndIndependentOfVideoChain()
    {
        MediaInfo media = MakeMedia(video: [Sdr1080p], subs: [EnglishSub, FrenchSub]);
        SubtitleOutput subOutput = new(
            Policy: SubtitlePolicy.Extract,
            Codec: SubtitleCodecType.WebVtt,
            AllowedLanguages: [],
            IncludeForced: true,
            OcrLanguage: null,
            PlaylistNameTemplate: "subs/{lang}"
        );
        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "Subs",
            Container: Container.HlsTs,
            Video: SingleOutput1080pH264,
            Audio: [DefaultAudioOutput],
            Subtitles: [subOutput, subOutput]
        );

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, [H264Software]);

        IEnumerable<ExecutionNode> subNodes = nodes.Where(n =>
            n.Operation == OperationType.SubtitleExtract
        );
        subNodes.Should().HaveCount(2);

        // Subtitle nodes must have no dependencies on video nodes
        ExecutionNode decodeNode = nodes.Single(n => n.Operation == OperationType.Decode);
        foreach (ExecutionNode subNode in subNodes)
        {
            subNode.DependsOn.Should().NotContain(decodeNode.Id);
        }
    }

    [Fact]
    public void ProfileWithThumbnails_ThumbnailCaptureNodePresent()
    {
        MediaInfo media = MakeMedia(video: [Sdr1080p]);
        ThumbnailOutput thumbnails = new(Width: 320, IntervalSeconds: 10);
        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "With Thumbs",
            Container: Container.HlsTs,
            Video: SingleOutput1080pH264,
            Audio: [],
            Subtitles: [],
            Thumbnails: thumbnails
        );

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, [H264Software]);

        ExecutionNode thumbNode = nodes.Single(n => n.Operation == OperationType.ThumbnailCapture);
        thumbNode.Parameters["width"].Should().Be("320");
        thumbNode.Parameters["interval"].Should().Be("10");
    }

    [Fact]
    public void ProfileWithoutThumbnails_NoThumbnailCaptureNode()
    {
        MediaInfo media = MakeMedia(video: [Sdr1080p]);
        EncodingProfile profile = SingleOutputProfile;

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, [H264Software]);

        nodes.Should().NotContain(n => n.Operation == OperationType.ThumbnailCapture);
    }

    [Fact]
    public void ChaptersPresent_ChapterExtractNodeAdded()
    {
        MediaInfo media = MakeMedia(video: [Sdr1080p], chapters: [Chapter]);
        EncodingProfile profile = SingleOutputProfile;

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, [H264Software]);

        nodes.Should().Contain(n => n.Operation == OperationType.ChapterExtract);
    }

    [Fact]
    public void NoChapters_NoChapterExtractNode()
    {
        MediaInfo media = MakeMedia(video: [Sdr1080p], chapters: []);
        EncodingProfile profile = SingleOutputProfile;

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, [H264Software]);

        nodes.Should().NotContain(n => n.Operation == OperationType.ChapterExtract);
    }

    [Fact]
    public void AllNodes_HaveUniqueIds()
    {
        MediaInfo media = MakeMedia(
            video: [Sdr1080p],
            subs: [EnglishSub, FrenchSub],
            chapters: [Chapter]
        );
        SubtitleOutput subOutput = new(
            Policy: SubtitlePolicy.Extract,
            Codec: SubtitleCodecType.WebVtt,
            AllowedLanguages: [],
            IncludeForced: true,
            OcrLanguage: null,
            PlaylistNameTemplate: "subs/{lang}"
        );
        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "Full",
            Container: Container.HlsTs,
            Video: SingleOutput1080pH264,
            Audio: [DefaultAudioOutput],
            Subtitles: [subOutput, subOutput],
            Thumbnails: new(320, 10)
        );

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, [H264Software]);

        IEnumerable<string> ids = nodes.Select(n => n.Id);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SingleOutputSmallerResolution_ScaleNodeAdded()
    {
        // 4K source encoded to 1080p → needs Scale node
        VideoStreamInfo source4k = Hdr4K with
        {
            ColorPrimaries = "bt709",
            ColorTransfer = "bt709",
            ColorSpace = "bt709",
        };
        MediaInfo media = MakeMedia(video: [source4k]);
        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "Scale Down",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H265,
                Width: 1920,
                Height: 1080,
                RateControl: V2RateControlMode.Crf,
                Crf: 22,
                BitrateKbps: 4000,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: null,
                CodecProfile: CodecProfile.High,
                Level: null,
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: "video/{label}",
                PlaylistNameTemplate: "video/{label}/playlist"
            ),
            Audio: [],
            Subtitles: []
        );

        List<ExecutionNode> nodes = Builder.BuildGraph(media, profile, [H264Software]);

        nodes.Should().Contain(n => n.Operation == OperationType.Scale);
    }
}
