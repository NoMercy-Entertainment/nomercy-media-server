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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Hdr;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Stages;
using AutoLadderConfig = NoMercy.Encoder.Profiles.AutoLadderConfig;
using BitrateStrategy = NoMercy.Encoder.Profiles.BitrateStrategy;
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using Container = NoMercy.Encoder.Profiles.Container;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using LadderConfig = NoMercy.Encoder.Profiles.LadderConfig;
using LadderMode = NoMercy.Encoder.Profiles.LadderMode;
using LadderRung = NoMercy.Encoder.Profiles.LadderRung;
using LadderTiers = NoMercy.Encoder.Profiles.LadderTiers;
using VideoOutput = NoMercy.Encoder.Profiles.VideoOutput;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

public class PlanStageAutoLadderTests
{
    private readonly Mock<ICodecResolver> _codecResolver = new();
    private readonly Mock<IHardwareCapabilities> _hardware = new();
    private readonly PlanStage _stage;

    public PlanStageAutoLadderTests()
    {
        _hardware.Setup(h => h.HasGpu).Returns(false);
        _hardware.Setup(h => h.CpuCores).Returns(8);
        _hardware.Setup(h => h.Gpus).Returns([]);
        _hardware.Setup(h => h.SupportsHardwareEncoding(It.IsAny<VideoCodecType>())).Returns(false);
        _hardware
            .Setup(h => h.GetGpuForCodec(It.IsAny<VideoCodecType>()))
            .Returns((GpuDevice?)null);

        _codecResolver
            .Setup(r =>
                r.Resolve(
                    It.IsAny<VideoCodecType>(),
                    It.IsAny<IHardwareCapabilities>(),
                    It.IsAny<EncoderPreference>()
                )
            )
            .Returns(
                new ResolvedCodec(
                    FfmpegEncoderName: "libx264",
                    EncoderInfo: new(
                        FfmpegName: "libx264",
                        RequiredVendor: null,
                        Presets: ["medium"],
                        Profiles: ["high"],
                        Levels: ["4.1"],
                        QualityRange: new(0, 51, 23),
                        SupportedRateControl: [RateControlMode.Crf],
                        Supports10Bit: false,
                        SupportsHdr: false,
                        MaxConcurrentSessions: int.MaxValue,
                        PixelFormat10Bit: "yuv420p10le",
                        VendorSpecificFlags: new()
                    ),
                    Device: null,
                    DefaultRateControl: RateControlMode.Crf
                )
            );

        _stage = new(
            new(),
            new(),
            new(),
            _codecResolver.Object,
            _hardware.Object,
            new TonemapSelector(),
            new Mock<IFfmpegCapabilities>().Object,
            new AbrLadderGenerator(),
            new NoOpCropDetector(),
            NullLogger<PlanStage>.Instance
        );
    }

    [Fact]
    public async Task AutoLadder_Off_PreservesManualVariants()
    {
        EncodingProfile profile = BuildProfile(
            autoLadder: false,
            rungs: [BuildVideo(1920, 1080), BuildVideo(1280, 720)]
        );

        OutputPlan plan = await RunPlan(BuildMedia(1920, 1080), profile);

        Assert.Equal(2, plan.VideoOutputs.Length);
        Assert.Contains(plan.VideoOutputs, v => v.Height == 1080);
        Assert.Contains(plan.VideoOutputs, v => v.Height == 720);
    }

    [Fact]
    public async Task AutoLadder_On_Expand1080pReference_Produces360_480_720_1080()
    {
        EncodingProfile profile = BuildProfile(autoLadder: true, rungs: [BuildVideo(1920, 1080)]);

        OutputPlan plan = await RunPlan(BuildMedia(1920, 1080, bitrateKbps: 6000), profile);

        int[] heights = [.. plan.VideoOutputs.Select(v => v.Height)];
        Assert.Contains(360, heights);
        Assert.Contains(480, heights);
        Assert.Contains(720, heights);
        Assert.Contains(1080, heights);
    }

    [Fact]
    public async Task AutoLadder_On_720pSource_SkipsHigherTiers()
    {
        EncodingProfile profile = BuildProfile(autoLadder: true, rungs: [BuildVideo(1280, 720)]);

        OutputPlan plan = await RunPlan(BuildMedia(1280, 720, bitrateKbps: 3000), profile);

        Assert.All(plan.VideoOutputs, v => Assert.True(v.Height <= 720));
        Assert.DoesNotContain(1080, plan.VideoOutputs.Select(v => v.Height));
    }

    [Fact]
    public async Task AutoLadder_RotatedSource_UsesPortraitDimensions()
    {
        EncodingProfile profile = BuildProfile(autoLadder: true, rungs: [BuildVideo(1920, 1080)]);

        OutputPlan plan = await RunPlan(BuildMedia(1920, 1080, rotation: -90), profile);

        Assert.All(plan.VideoOutputs, output => Assert.True(output.Width < output.Height));
        Assert.Contains(plan.VideoOutputs, output => output.Width == 1080 && output.Height == 1920);
    }

    [Fact]
    public async Task AutoConfigLadder_RotatedSource_UsesPortraitDimensions()
    {
        EncodingProfile profile = BuildProfile(
            autoLadder: true,
            rungs: [BuildVideo(1920, 1080)]
        ) with
        {
            Ladder = new()
            {
                Mode = LadderMode.Auto,
                Rungs = [BuildVideo(1920, 1080)],
                AutoConfig = new()
                {
                    NeverUpscale = true,
                    NeverUpsource = false,
                    MinTierGapPercent = 0,
                },
            },
        };

        OutputPlan plan = await RunPlan(BuildMedia(1920, 1080, rotation: 90), profile);

        Assert.All(plan.VideoOutputs, output => Assert.True(output.Width < output.Height));
        Assert.Contains(plan.VideoOutputs, output => output.Width == 1080 && output.Height == 1920);
    }

    [Fact]
    public async Task AutoLadder_On_MultipleVariants_FallsBackToManual()
    {
        // AutoLadder requires exactly 1 reference profile — with more than 1,
        // the stage logs a warning and keeps the manual variants.
        EncodingProfile profile = BuildProfile(
            autoLadder: true,
            rungs: [BuildVideo(1920, 1080), BuildVideo(1280, 720)]
        );

        OutputPlan plan = await RunPlan(BuildMedia(1920, 1080), profile);

        Assert.Equal(2, plan.VideoOutputs.Length);
    }

    [Fact]
    public async Task AutoLadder_On_AudioOnlySource_NoExpansion()
    {
        EncodingProfile profile = BuildProfile(autoLadder: true, rungs: [BuildVideo(1920, 1080)]);

        // Source has no video streams → auto-ladder passthrough.
        OutputPlan plan = await RunPlan(BuildAudioOnlyMedia(), profile);

        Assert.Empty(plan.VideoOutputs);
    }

    private async Task<OutputPlan> RunPlan(MediaInfo media, EncodingProfile profile)
    {
        ValidateInput input = new(media, profile);
        EncodingContext context = EncodingContext.Create();
        StageResult result = await _stage.ExecuteAsync(input, context, CancellationToken.None);

        StageSuccess<ExecutionPlan> success = Assert.IsType<StageSuccess<ExecutionPlan>>(result);
        return success.Value.OutputPlan;
    }

    private static MediaInfo BuildMedia(
        int width,
        int height,
        long bitrateKbps = 6000,
        int rotation = 0
    ) =>
        new(
            FilePath: "/media/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: bitrateKbps + 500,
            FileSizeBytes: 4_000_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "h264",
                    Width: width,
                    Height: height,
                    FrameRate: 24.0,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    ColorPrimaries: null,
                    ColorTransfer: null,
                    ColorSpace: null,
                    IsDefault: true,
                    BitRateKbps: bitrateKbps,
                    Rotation: rotation
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static MediaInfo BuildAudioOnlyMedia() =>
        new(
            FilePath: "/media/song.flac",
            Format: "flac",
            Duration: TimeSpan.FromMinutes(4),
            OverallBitRateKbps: 800,
            FileSizeBytes: 20_000_000,
            VideoStreams: [],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static EncodingProfile BuildProfile(bool autoLadder, LadderRung[] rungs) =>
        new(
            Id: Ulid.NewUlid(),
            Name: "Test",
            Container: Container.HlsTs,
            Video: null,
            Audio: [],
            Subtitles: [],
            Ladder: new() { Mode = autoLadder ? LadderMode.Auto : LadderMode.Manual, Rungs = rungs }
        );

    private static LadderRung BuildVideo(int width, int height) =>
        new(
            Width: width,
            Height: height,
            Codec: VideoCodecType.H264,
            BitrateKbps: 4000,
            MaxBitrateKbps: 6000,
            BufferSizeKbps: 8000,
            Framerate: 24.0,
            Preset: "medium",
            CodecProfile: CodecProfile.High,
            BitDepth: 8,
            PixelFormat: "yuv420p"
        );
}

/// <summary>
/// Verifies that <see cref="AutoLadderExpander.Expand"/> routes to the correct
/// <see cref="IAbrLadderGenerator"/> method depending on whether
/// <see cref="LadderConfig.AutoConfig"/> is set.
/// </summary>
public class PlanStageAutoLadderRoutingTests
{
    private readonly Mock<IAbrLadderGenerator> _abrGenerator = new();
    private readonly Mock<ICodecResolver> _codecResolver = new();
    private readonly Mock<IHardwareCapabilities> _hardware = new();

    private PlanStage BuildStage()
    {
        _hardware.Setup(h => h.HasGpu).Returns(false);
        _hardware.Setup(h => h.CpuCores).Returns(8);
        _hardware.Setup(h => h.Gpus).Returns([]);
        _hardware.Setup(h => h.SupportsHardwareEncoding(It.IsAny<VideoCodecType>())).Returns(false);
        _hardware
            .Setup(h => h.GetGpuForCodec(It.IsAny<VideoCodecType>()))
            .Returns((GpuDevice?)null);

        _codecResolver
            .Setup(r =>
                r.Resolve(
                    It.IsAny<VideoCodecType>(),
                    It.IsAny<IHardwareCapabilities>(),
                    It.IsAny<EncoderPreference>()
                )
            )
            .Returns(
                new ResolvedCodec(
                    FfmpegEncoderName: "libx264",
                    EncoderInfo: new(
                        FfmpegName: "libx264",
                        RequiredVendor: null,
                        Presets: ["medium"],
                        Profiles: ["high"],
                        Levels: ["4.1"],
                        QualityRange: new(0, 51, 23),
                        SupportedRateControl: [RateControlMode.Crf],
                        Supports10Bit: false,
                        SupportsHdr: false,
                        MaxConcurrentSessions: int.MaxValue,
                        PixelFormat10Bit: "yuv420p10le",
                        VendorSpecificFlags: new()
                    ),
                    Device: null,
                    DefaultRateControl: RateControlMode.Crf
                )
            );

        return new(
            new(),
            new(),
            new(),
            _codecResolver.Object,
            _hardware.Object,
            new TonemapSelector(),
            new Mock<IFfmpegCapabilities>().Object,
            _abrGenerator.Object,
            new NoOpCropDetector(),
            NullLogger<PlanStage>.Instance
        );
    }

    private static MediaInfo Build1080pMedia() =>
        new(
            FilePath: "/media/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 4500,
            FileSizeBytes: 3_000_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "h264",
                    Width: 1920,
                    Height: 1080,
                    FrameRate: 24.0,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    ColorPrimaries: null,
                    ColorTransfer: null,
                    ColorSpace: null,
                    IsDefault: true,
                    BitRateKbps: 4000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static LadderRung[] ThreeKnownRungs() =>
        [
            new(640, 360, VideoCodecType.H264, 400, 600, 800, 24.0),
            new(1280, 720, VideoCodecType.H264, 2000, 3000, 4000, 24.0),
            new(1920, 1080, VideoCodecType.H264, 5000, 7500, 10000, 24.0),
        ];

    // ── New path (AutoConfig set) ──────────────────────────────────────────

    [Fact]
    public async Task AutoConfig_Set_CallsGenerateLadder_NotGenerate()
    {
        AutoLadderConfig autoConfig = new()
        {
            Tiers = LadderTiers.Standard,
            BitrateStrategy = BitrateStrategy.AppleHlsRecommended,
        };

        LadderRung[] mockedRungs = ThreeKnownRungs();

        _abrGenerator
            .Setup(g =>
                g.GenerateLadder(
                    It.IsAny<MediaInfo>(),
                    It.IsAny<VideoCodecType>(),
                    It.IsAny<AutoLadderConfig>(),
                    It.IsAny<VideoOutput?>()
                )
            )
            .Returns(mockedRungs);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "AutoConfigTest",
            Container: Container.HlsTs,
            Video: new(
                Policy: NoMercy.Encoder.Profiles.StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 1920,
                Height: 1080,
                RateControl: NoMercy.Encoder.Profiles.RateControlMode.Crf,
                Crf: 23,
                BitrateKbps: 4000,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "medium",
                CodecProfile: CodecProfile.High,
                Level: "4.1",
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: "video/:label:",
                PlaylistNameTemplate: "video/:label:/playlist"
            ),
            Audio: [],
            Subtitles: [],
            Ladder: new() { Mode = LadderMode.Auto, AutoConfig = autoConfig }
        );

        PlanStage stage = BuildStage();
        ValidateInput input = new(Build1080pMedia(), profile);
        EncodingContext context = EncodingContext.Create();

        StageResult result = await stage.ExecuteAsync(input, context, CancellationToken.None);

        StageSuccess<ExecutionPlan> success = Assert.IsType<StageSuccess<ExecutionPlan>>(result);
        OutputPlan outputPlan = success.Value.OutputPlan;

        // GenerateLadder called once with the expected codec + AutoConfig.
        _abrGenerator.Verify(
            g =>
                g.GenerateLadder(
                    It.IsAny<MediaInfo>(),
                    VideoCodecType.H264,
                    autoConfig,
                    It.IsAny<VideoOutput?>()
                ),
            Times.Once
        );

        // Legacy Generate must NOT be called.
        _abrGenerator.Verify(
            g => g.Generate(It.IsAny<MediaInfo>(), It.IsAny<VideoOutput>()),
            Times.Never
        );

        // Resulting rungs must match the mocked array (by height).
        int[] heights = [.. outputPlan.VideoOutputs.Select(v => v.Height)];
        Assert.Contains(360, heights);
        Assert.Contains(720, heights);
        Assert.Contains(1080, heights);
    }

    // ── Legacy path (AutoConfig null) ─────────────────────────────────────

    [Fact]
    public async Task AutoConfig_Null_CallsGenerate_NotGenerateLadder()
    {
        _abrGenerator
            .Setup(g => g.Generate(It.IsAny<MediaInfo>(), It.IsAny<VideoOutput>()))
            .Returns([
                new(
                    Policy: NoMercy.Encoder.Profiles.StreamPolicy.Transcode,
                    Codec: VideoCodecType.H264,
                    Width: 1920,
                    Height: 1080,
                    RateControl: NoMercy.Encoder.Profiles.RateControlMode.Crf,
                    Crf: 23,
                    BitrateKbps: 4000,
                    MaxBitrateKbps: null,
                    BufferSizeKbps: null,
                    Preset: "medium",
                    CodecProfile: CodecProfile.High,
                    Level: "4.1",
                    Tune: null,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    KeyframeIntervalSeconds: 2,
                    ConvertHdrToSdr: false,
                    SegmentNameTemplate: "video/:label:",
                    PlaylistNameTemplate: "video/:label:/playlist"
                ),
            ]);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "LegacyAutoTest",
            Container: Container.HlsTs,
            Video: new(
                Policy: NoMercy.Encoder.Profiles.StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 1920,
                Height: 1080,
                RateControl: NoMercy.Encoder.Profiles.RateControlMode.Crf,
                Crf: 23,
                BitrateKbps: 4000,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "medium",
                CodecProfile: CodecProfile.High,
                Level: "4.1",
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: "video/:label:",
                PlaylistNameTemplate: "video/:label:/playlist"
            ),
            Audio: [],
            Subtitles: [],
            Ladder: new() { Mode = LadderMode.Auto, AutoConfig = null }
        );

        PlanStage stage = BuildStage();
        ValidateInput input = new(Build1080pMedia(), profile);
        EncodingContext context = EncodingContext.Create();

        StageResult result = await stage.ExecuteAsync(input, context, CancellationToken.None);

        Assert.IsType<StageSuccess<ExecutionPlan>>(result);

        // Legacy Generate called once.
        _abrGenerator.Verify(
            g => g.Generate(It.IsAny<MediaInfo>(), It.IsAny<VideoOutput>()),
            Times.Once
        );

        // New GenerateLadder must NOT be called.
        _abrGenerator.Verify(
            g =>
                g.GenerateLadder(
                    It.IsAny<MediaInfo>(),
                    It.IsAny<VideoCodecType>(),
                    It.IsAny<AutoLadderConfig>(),
                    It.IsAny<VideoOutput?>()
                ),
            Times.Never
        );
    }
}
