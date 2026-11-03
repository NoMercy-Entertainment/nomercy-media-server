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
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using Container = NoMercy.Encoder.Profiles.Container;
using StreamPolicy = NoMercy.Encoder.Profiles.StreamPolicy;
using V2RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

/// <summary>
/// The profile CRF is translated to the hardware encoder's native quality
/// scale exactly once, by the <see cref="IQualityScaler"/> chain, and the
/// translated value is what reaches the vendor flag.
///
/// Before the fix the value was scaled twice: PlanStage translated it with
/// the scaler and then <c>EncoderArgumentResolver.ResolveQuality</c> scaled
/// the already-native value a second time. H.264 / HEVC on NVENC, AMF and
/// QSV hid the bug (their native max equals the 0-51 reference, so both
/// passes were identities); every encoder with a different range got the
/// wrong quality: av1_amf CRF 35 became -qp 255 (clamped, worst quality),
/// hevc_videotoolbox CRF 28 became -q:v 88 instead of 45, av1_nvenc CRF 35
/// became -cq 23 instead of 28.
/// </summary>
public class PlanStageQualityTranslationTests
{
    [Theory]
    [InlineData("av1_amf", VideoCodecType.Av1, RateControlMode.Cqp, 0, 255, 35, "-qp", "142")]
    [InlineData(
        "hevc_videotoolbox",
        VideoCodecType.H265,
        RateControlMode.QualityLevel,
        0,
        100,
        28,
        "-q:v",
        "45"
    )]
    [InlineData("av1_nvenc", VideoCodecType.Av1, RateControlMode.Cq, 0, 51, 35, "-cq", "28")]
    [InlineData(
        "vp9_qsv",
        VideoCodecType.Vp9,
        RateControlMode.Icq,
        1,
        51,
        33,
        "-global_quality",
        "27"
    )]
    [InlineData("h264_nvenc", VideoCodecType.H264, RateControlMode.Cq, 0, 51, 23, "-cq", "23")]
    public async Task Hardware_quality_is_the_scaler_value_emitted_once(
        string ffmpegName,
        VideoCodecType codec,
        RateControlMode defaultRateControl,
        int qualityMin,
        int qualityMax,
        int crf,
        string flag,
        string expected
    )
    {
        PlanStage stage = BuildPlanStage(
            HardwareCodec(ffmpegName, defaultRateControl, qualityMin, qualityMax)
        );

        VideoOutputPlan video = await RunPlanAsync(stage, FakeInput(codec, crf));

        video.Crf.Should().Be(0, "a hardware encoder never receives -crf");
        video
            .ExtraFlags.Should()
            .ContainKey(flag)
            .WhoseValue.Should()
            .Be(expected, "the scaler's value must reach ffmpeg without a second scaling pass");
    }

    private static PlanStage BuildPlanStage(ResolvedCodec codec)
    {
        Mock<ICodecResolver> codecResolver = new();
        codecResolver
            .Setup(r =>
                r.Resolve(
                    It.IsAny<VideoCodecType>(),
                    It.IsAny<IHardwareCapabilities>(),
                    It.IsAny<EncoderPreference>()
                )
            )
            .Returns(codec);

        Mock<IHardwareCapabilities> hardware = new();
        hardware.Setup(h => h.HasGpu).Returns(false);
        hardware.Setup(h => h.CpuCores).Returns(8);
        hardware.Setup(h => h.Gpus).Returns([]);
        hardware.Setup(h => h.SupportsHardwareEncoding(It.IsAny<VideoCodecType>())).Returns(false);
        hardware.Setup(h => h.GetGpuForCodec(It.IsAny<VideoCodecType>())).Returns((GpuDevice?)null);

        // The production scaler chain, in registration order (specific first,
        // linear fallback last) — the same shape ServiceCollectionExtensions wires.
        IQualityScaler[] scalers =
        [
            new NvencQualityScaler(),
            new QsvQualityScaler(),
            new VideoToolboxQualityScaler(),
            new AmfAv1QualityScaler(),
            new SvtAv1QualityScaler(),
            new LinearQualityScaler(),
        ];

        return new(
            new(),
            new(),
            new(),
            codecResolver.Object,
            hardware.Object,
            new TonemapSelector(),
            new Mock<IFfmpegCapabilities>().Object,
            new AbrLadderGenerator(),
            new NoOpCropDetector(),
            NullLogger<PlanStage>.Instance,
            qualityScalerResolver: new QualityScalerResolver(scalers)
        );
    }

    private static ResolvedCodec HardwareCodec(
        string ffmpegName,
        RateControlMode defaultRateControl,
        int qualityMin,
        int qualityMax
    ) =>
        new(
            FfmpegEncoderName: ffmpegName,
            EncoderInfo: new(
                FfmpegName: ffmpegName,
                RequiredVendor: null,
                Presets: ["p1", "p4", "p7"],
                Profiles: ["main"],
                Levels: ["4.1"],
                QualityRange: new(qualityMin, qualityMax, qualityMax / 2),
                SupportedRateControl: [defaultRateControl],
                Supports10Bit: false,
                SupportsHdr: false,
                MaxConcurrentSessions: 3,
                PixelFormat10Bit: "yuv420p10le",
                VendorSpecificFlags: new()
            ),
            Device: new(
                Vendor: GpuVendor.Nvidia,
                Name: "TestGpu",
                VramMb: 8192,
                MaxEncoderSessions: 3,
                SupportedCodecs: [VideoCodecType.H264, VideoCodecType.H265, VideoCodecType.Av1]
            ),
            DefaultRateControl: defaultRateControl
        );

    private static MediaInfo FakeMediaInfo() =>
        new(
            FilePath: "/media/movie.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 10000,
            FileSizeBytes: 1_000_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "h264",
                    Width: 1920,
                    Height: 1080,
                    FrameRate: 23.976,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    ColorPrimaries: null,
                    ColorTransfer: null,
                    ColorSpace: null,
                    IsDefault: true,
                    BitRateKbps: 8000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static ValidateInput FakeInput(VideoCodecType codec, int crf) =>
        new(
            Media: FakeMediaInfo(),
            Profile: new(
                Id: Ulid.NewUlid(),
                Name: "Test",
                Container: Container.HlsTs,
                Video: new(
                    Policy: StreamPolicy.Transcode,
                    Codec: codec,
                    Width: 1920,
                    Height: null,
                    RateControl: V2RateControlMode.Crf,
                    Crf: crf,
                    BitrateKbps: 0,
                    MaxBitrateKbps: null,
                    BufferSizeKbps: null,
                    Preset: "p4",
                    CodecProfile: CodecProfile.Auto,
                    Level: null,
                    Tune: null,
                    BitDepth: 8,
                    PixelFormat: null,
                    KeyframeIntervalSeconds: 2,
                    ConvertHdrToSdr: false,
                    SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                    PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
                ),
                Audio: [],
                Subtitles: []
            )
        );

    private static async Task<VideoOutputPlan> RunPlanAsync(PlanStage stage, ValidateInput input)
    {
        EncodingContext ctx = EncodingContext.Create();
        StageResult result = await stage.ExecuteAsync(input, ctx, CancellationToken.None);
        ExecutionPlan plan = ((StageSuccess<ExecutionPlan>)result).Value;
        return plan.OutputPlan.VideoOutputs[0];
    }
}
