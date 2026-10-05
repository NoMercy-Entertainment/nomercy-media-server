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
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Hdr;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;
using ContainerCompatibility = NoMercy.Encoder.Profiles.ContainerCompatibility;
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using Container = NoMercy.Encoder.Profiles.Container;
using StreamPolicy = NoMercy.Encoder.Profiles.StreamPolicy;
using V2RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

/// <summary>
/// The invariant this whole class exists to prove: for EVERY validator-passing
/// profile in a combinatorial grid, the ffmpeg command the pipeline builds is
/// internally consistent — ffmpeg would accept it and it does what the profile
/// asked. This is the "no valid profile can produce broken args" guarantee,
/// exercised across codec x container x rate-control x bit-depth x dimensions
/// rather than one example at a time.
///
/// Each case runs the REAL PlanStage -> output strategy -> FfmpegCommandBuilder
/// and asserts the argv-level invariants. A regression in any arg-generation
/// stage trips one of them.
/// </summary>
public class ProfileToArgvInvariantTests
{
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
            NullLogger<PlanStage>.Instance
        );
    }

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
                    Width: 3840,
                    Height: 2160,
                    FrameRate: 23.976,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    ColorPrimaries: null,
                    ColorTransfer: null,
                    ColorSpace: null,
                    IsDefault: true,
                    BitRateKbps: 30000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static ResolvedCodec SoftwareCodec(string ffmpegName) =>
        new(
            FfmpegEncoderName: ffmpegName,
            EncoderInfo: new(
                FfmpegName: ffmpegName,
                RequiredVendor: null,
                Presets: ["slow", "medium", "fast"],
                Profiles: ["baseline", "main", "high"],
                Levels: ["4.1"],
                QualityRange: new(0, 51, 23),
                SupportedRateControl: [RateControlMode.Crf, RateControlMode.Vbr],
                Supports10Bit: true,
                SupportsHdr: false,
                MaxConcurrentSessions: int.MaxValue,
                PixelFormat10Bit: "yuv420p10le",
                VendorSpecificFlags: new()
            ),
            Device: null,
            DefaultRateControl: RateControlMode.Crf
        );

    private static ResolvedCodec HardwareCodec(string ffmpegName) =>
        new(
            FfmpegEncoderName: ffmpegName,
            EncoderInfo: new(
                FfmpegName: ffmpegName,
                RequiredVendor: null,
                Presets: ["p1", "p4", "p7"],
                Profiles: ["main", "high"],
                Levels: ["4.1"],
                QualityRange: new(0, 51, 26),
                SupportedRateControl: [RateControlMode.Cq],
                Supports10Bit: true,
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
                SupportedCodecs: [VideoCodecType.H264, VideoCodecType.H265]
            ),
            DefaultRateControl: RateControlMode.Cq
        );

    // Only H264/H265 have HW encoders modelled here; AV1/VP9 exercise the
    // software path (and the codec-specific quality scales) in this grid.
    private static bool HasHardwareEncoder(VideoCodecType codec) =>
        codec is VideoCodecType.H264 or VideoCodecType.H265;

    private static string SoftwareEncoderName(VideoCodecType codec) =>
        codec switch
        {
            VideoCodecType.H264 => "libx264",
            VideoCodecType.H265 => "libx265",
            VideoCodecType.Av1 => "libsvtav1",
            VideoCodecType.Vp9 => "libvpx-vp9",
            _ => "libx264",
        };

    public static IEnumerable<object[]> Grid()
    {
        VideoCodecType[] codecs =
        [
            VideoCodecType.H264,
            VideoCodecType.H265,
            VideoCodecType.Av1,
            VideoCodecType.Vp9,
        ];
        Container[] containers =
        [
            Container.HlsTs,
            Container.HlsFmp4,
            Container.Mp4,
            Container.Mkv,
            Container.Dash,
        ];
        V2RateControlMode[] rateControls = [V2RateControlMode.Crf, V2RateControlMode.Vbr];
        (int crf, int bitrate)[] qualityPairs =
        [
            (22, 0), // pure CRF
            (0, 6000), // pure bitrate
            (22, 6000), // capped-CRF (the dangerous combo)
        ];
        int[] widths = [1920, 1921, 1280]; // include an odd width
        int[] bitDepths = [8, 10];
        bool[] hardwareChoices = [false, true];

        foreach (VideoCodecType codec in codecs)
        foreach (Container container in containers)
        {
            // Only generate codec/container pairs the support matrix allows —
            // the invariant is about VALID profiles, not ones validation rejects.
            if (!ContainerCompatibility.SupportsVideo(container, codec))
                continue;

            foreach (V2RateControlMode rc in rateControls)
            foreach ((int crf, int bitrate) in qualityPairs)
            foreach (int width in widths)
            foreach (int bitDepth in bitDepths)
            foreach (bool hardware in hardwareChoices)
            {
                // Skip incoherent rate-control/quality pairs the validator rejects.
                if (rc == V2RateControlMode.Vbr && bitrate == 0)
                    continue;
                if (rc == V2RateControlMode.Crf && crf == 0)
                    continue;
                // Only H264/H265 have HW encoders in this grid.
                if (hardware && !HasHardwareEncoder(codec))
                    continue;

                yield return [codec, container, rc, crf, bitrate, width, bitDepth, hardware];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public async Task EveryProfile_ProducesInternallyConsistentArgs(
        VideoCodecType codec,
        Container container,
        V2RateControlMode rc,
        int crf,
        int bitrate,
        int width,
        int bitDepth,
        bool hardware
    )
    {
        ResolvedCodec resolved = hardware
            ? HardwareCodec(codec == VideoCodecType.H264 ? "h264_nvenc" : "hevc_nvenc")
            : SoftwareCodec(SoftwareEncoderName(codec));

        PlanStage stage = BuildPlanStage(resolved);

        ValidateInput input = new(
            Media: FakeMediaInfo(),
            Profile: new(
                Id: Ulid.NewUlid(),
                Name: "Grid",
                Container: container,
                Video: new(
                    Policy: StreamPolicy.Transcode,
                    Codec: codec,
                    Width: width,
                    Height: null,
                    RateControl: rc,
                    Crf: crf,
                    BitrateKbps: bitrate,
                    MaxBitrateKbps: null,
                    BufferSizeKbps: null,
                    Preset: "fast",
                    CodecProfile: CodecProfile.Auto,
                    Level: null,
                    Tune: null,
                    BitDepth: bitDepth,
                    PixelFormat: null,
                    KeyframeIntervalSeconds: 2,
                    ConvertHdrToSdr: false,
                    SegmentNameTemplate: "video/:framesize:",
                    PlaylistNameTemplate: "video/:framesize:/playlist"
                ),
                Audio: [],
                Subtitles: []
            )
        );

        EncodingContext ctx = EncodingContext.Create();
        StageResult result = await stage.ExecuteAsync(input, ctx, CancellationToken.None);
        result.Should().BeOfType<StageSuccess<ExecutionPlan>>();
        ExecutionPlan plan = ((StageSuccess<ExecutionPlan>)result).Value;

        IOutputStrategy strategy = ResolveStrategy(plan.OutputPlan.Format);
        FfmpegCommandBuilder builder = new();
        builder.AddInput(new("/input.mkv"));
        strategy.ConfigureOutput(builder, plan.OutputPlan, "/output");
        string[] args = builder.Build("ffmpeg").Arguments;

        AssertInvariants(args, plan.OutputPlan);
    }

    private static void AssertInvariants(string[] args, OutputPlan plan)
    {
        // 1. Rate control is exactly one mode per video output: never -crf AND
        //    -b:v together (that forces ABR and voids the CRF target).
        bool hasCrf = args.Contains("-crf");
        bool hasTargetBitrate = args.Contains("-b:v");
        (hasCrf && hasTargetBitrate)
            .Should()
            .BeFalse("a video output must not carry both -crf and -b:v");

        // 2. A hardware quality flag must not coexist with -b:v either.
        bool hasHwQuality = args.Contains("-cq") || args.Contains("-qp");
        (hasHwQuality && hasTargetBitrate)
            .Should()
            .BeFalse("a capped-quality HW encode must not carry a redundant -b:v");

        // 3. No builder-managed flag is emitted more than once per its value slot.
        //    A duplicate managed flag means a custom-arg / typed-field collision.
        foreach (
            string managed in new[] { "-crf", "-b:v", "-profile:v", "-level", "-pix_fmt", "-g" }
        )
        {
            int count = args.Count(a => a == managed);
            count.Should().BeLessThanOrEqualTo(1, $"{managed} must appear at most once per output");
        }

        // 4. Every emitted output dimension is even (odd luma dims abort ffmpeg
        //    on 4:2:0). The plan is the source of truth for width/height.
        foreach (VideoOutputPlan video in plan.VideoOutputs)
        {
            (video.Width % 2).Should().Be(0, "odd width aborts the encode");
            (video.Height % 2).Should().Be(0, "odd height aborts the encode");
            video.Width.Should().BeGreaterThan(0);
            video.Height.Should().BeGreaterThan(0);
        }
    }

    private static IOutputStrategy ResolveStrategy(OutputFormat format)
    {
        IStorage storage = TestStorageFactory.CreateLocal();
        return format switch
        {
            OutputFormat.Hls => new HlsOutputStrategy(storage),
            OutputFormat.Mp4 => new Mp4OutputStrategy(storage),
            OutputFormat.Mkv => new MkvOutputStrategy(storage),
            OutputFormat.Dash => new DashOutputStrategy(storage),
            _ => new HlsOutputStrategy(storage),
        };
    }
}
