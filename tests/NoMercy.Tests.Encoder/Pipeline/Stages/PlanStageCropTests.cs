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
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Hdr;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Stages;
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using Container = NoMercy.Encoder.Profiles.Container;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using StreamPolicy = NoMercy.Encoder.Profiles.StreamPolicy;
using V2RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

public class PlanStageCropTests
{
    [Fact]
    public async Task AutoDetectCropOff_DetectorNotInvoked()
    {
        Mock<ICropDetector> detector = new();
        PlanStage stage = BuildStage(detector.Object);

        EncodingProfile profile = BuildProfile(autoDetectCrop: false);
        OutputPlan plan = await RunPlan(stage, profile);

        detector.Verify(
            d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        plan.VideoOutputs[0].CropFilter.Should().BeNull();
    }

    [Fact]
    public async Task GlobalOptOut_DetectorNotInvoked_EvenWhenProfileOptsIn()
    {
        // EnableCropDetectSubscriber is the operator's single switch for crop
        // detection; a profile that asks for it must still be refused.
        Mock<ICropDetector> detector = new();
        PlanStage stage = BuildStage(
            detector.Object,
            new EncoderOptions { EnableCropDetectSubscriber = false }
        );

        OutputPlan plan = await RunPlan(stage, BuildProfile(autoDetectCrop: true));

        detector.Verify(
            d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        plan.VideoOutputs[0].CropFilter.Should().BeNull();
    }

    [Fact]
    public async Task GlobalOptOutLeftOn_ProfileOptIn_DetectorInvoked()
    {
        Mock<ICropDetector> detector = new();
        detector
            .Setup(d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new CropResult(0, 0, 0, 0, ShouldCrop: false));

        PlanStage stage = BuildStage(
            detector.Object,
            new EncoderOptions { EnableCropDetectSubscriber = true }
        );

        await RunPlan(stage, BuildProfile(autoDetectCrop: true));

        detector.Verify(
            d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task AutoDetectCropOn_ShouldCrop_PopulatesCropFilterOnAllOutputs()
    {
        Mock<ICropDetector> detector = new();
        detector
            .Setup(d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new CropResult(Width: 1920, Height: 800, X: 0, Y: 140, ShouldCrop: true));

        PlanStage stage = BuildStage(detector.Object);
        EncodingProfile profile = BuildProfile(autoDetectCrop: true);

        OutputPlan plan = await RunPlan(stage, profile);

        plan.VideoOutputs.Should().HaveCountGreaterThan(0);
        plan.VideoOutputs[0].CropFilter.Should().Be("1920:800:0:140");
        detector.Verify(
            d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task AutoDetectCropOn_NoCropNeeded_CropFilterNull()
    {
        Mock<ICropDetector> detector = new();
        detector
            .Setup(d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new CropResult(0, 0, 0, 0, ShouldCrop: false));

        PlanStage stage = BuildStage(detector.Object);
        EncodingProfile profile = BuildProfile(autoDetectCrop: true);

        OutputPlan plan = await RunPlan(stage, profile);

        plan.VideoOutputs[0].CropFilter.Should().BeNull();
    }

    [Fact]
    public async Task AutoDetectCropOn_DetectorThrows_ContinuesWithoutCrop()
    {
        Mock<ICropDetector> detector = new();
        detector
            .Setup(d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("boom"));

        PlanStage stage = BuildStage(detector.Object);
        EncodingProfile profile = BuildProfile(autoDetectCrop: true);

        OutputPlan plan = await RunPlan(stage, profile);

        plan.VideoOutputs[0].CropFilter.Should().BeNull();
    }

    [Fact]
    public async Task AutoDetectCropOn_BarsWithinTolerance_CropFilterNull()
    {
        // Source 1920x1080; detected content 1920x1000 → only 80px of vertical
        // bar, under the 100px threshold. A stray border must NOT force a crop
        // (which would disable stream-copy on every rung it touches).
        Mock<ICropDetector> detector = new();
        detector
            .Setup(d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new CropResult(Width: 1920, Height: 1000, X: 0, Y: 40, ShouldCrop: true));

        PlanStage stage = BuildStage(detector.Object);
        OutputPlan plan = await RunPlan(stage, BuildProfile(autoDetectCrop: true));

        plan.VideoOutputs[0].CropFilter.Should().BeNull();
    }

    [Fact]
    public async Task AutoDetectCropOn_HorizontalBarsOverThreshold_PopulatesCropFilter()
    {
        // Pillarbox: 1920x1080 source, content 1700x1080 → 220px horizontal bar,
        // over the threshold, so the crop is honoured even though it forces a
        // re-encode.
        Mock<ICropDetector> detector = new();
        detector
            .Setup(d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new CropResult(Width: 1700, Height: 1080, X: 110, Y: 0, ShouldCrop: true)
            );

        PlanStage stage = BuildStage(detector.Object);
        OutputPlan plan = await RunPlan(stage, BuildProfile(autoDetectCrop: true));

        plan.VideoOutputs[0].CropFilter.Should().Be("1700:1080:110:0");
    }

    [Fact]
    public async Task AutoDetectCropOn_HdrSource_PassesHdrFlagToDetector()
    {
        // The HDR flag must reach the detector so it can pick the HDR cropdetect
        // limit — the whole reason letterbox on HDR sources was being missed.
        bool? capturedHdr = null;
        Mock<ICropDetector> detector = new();
        detector
            .Setup(d =>
                d.DetectAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback((string _, Guid? _, bool? isHdr, CancellationToken _) => capturedHdr = isHdr)
            .ReturnsAsync(new CropResult(0, 0, 0, 0, ShouldCrop: false));

        PlanStage stage = BuildStage(detector.Object);
        ValidateInput input = new(BuildHdrMedia(), BuildProfile(autoDetectCrop: true));
        await stage.ExecuteAsync(input, EncodingContext.Create(), CancellationToken.None);

        capturedHdr.Should().BeTrue();
    }

    private static PlanStage BuildStage(ICropDetector cropDetector, EncoderOptions? options = null)
    {
        Mock<IHardwareCapabilities> hardware = new();
        hardware.Setup(h => h.HasGpu).Returns(false);
        hardware.Setup(h => h.CpuCores).Returns(8);
        hardware.Setup(h => h.Gpus).Returns([]);
        hardware.Setup(h => h.SupportsHardwareEncoding(It.IsAny<VideoCodecType>())).Returns(false);
        hardware.Setup(h => h.GetGpuForCodec(It.IsAny<VideoCodecType>())).Returns((GpuDevice?)null);

        Mock<ICodecResolver> codecResolver = new();
        codecResolver
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
            codecResolver.Object,
            hardware.Object,
            new TonemapSelector(),
            new Mock<IFfmpegCapabilities>().Object,
            new AbrLadderGenerator(),
            cropDetector,
            NullLogger<PlanStage>.Instance,
            options: options
        );
    }

    private static async Task<OutputPlan> RunPlan(PlanStage stage, EncodingProfile profile)
    {
        ValidateInput input = new(BuildMedia(), profile);
        EncodingContext context = EncodingContext.Create();
        StageResult result = await stage.ExecuteAsync(input, context, CancellationToken.None);

        StageSuccess<ExecutionPlan> success = Assert.IsType<StageSuccess<ExecutionPlan>>(result);
        return success.Value.OutputPlan;
    }

    private static MediaInfo BuildMedia() =>
        new(
            FilePath: "/media/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 8000,
            FileSizeBytes: 4_000_000_000,
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
                    BitRateKbps: 6000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static MediaInfo BuildHdrMedia() =>
        new(
            FilePath: "/media/test-hdr.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 40000,
            FileSizeBytes: 20_000_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "hevc",
                    Width: 3840,
                    Height: 2160,
                    FrameRate: 24.0,
                    BitDepth: 10,
                    PixelFormat: "yuv420p10le",
                    ColorPrimaries: "bt2020",
                    ColorTransfer: "smpte2084",
                    ColorSpace: "bt2020nc",
                    IsDefault: true,
                    BitRateKbps: 35000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static EncodingProfile BuildProfile(bool autoDetectCrop) =>
        new(
            Id: Ulid.NewUlid(),
            Name: "Crop Test",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 1920,
                Height: 1080,
                RateControl: V2RateControlMode.Crf,
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
                SegmentNameTemplate: "video/{label}",
                PlaylistNameTemplate: "video/{label}/playlist"
            ),
            Audio: [],
            Subtitles: [],
            AutoDetectCrop: autoDetectCrop
        );
}
