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
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using LadderMode = NoMercy.Encoder.Profiles.LadderMode;
using LadderRung = NoMercy.Encoder.Profiles.LadderRung;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

/// <summary>
/// Pins the real hardware-encoder init probe (<see cref="HardwareEncoderProbe"/>,
/// surfaced as <see cref="IHardwareCapabilities.UsableHardwareEncoders"/>) as
/// the SOLE authority for hardware encoder selection in <see cref="PlanStage"/>
/// — superseding the earlier GPU-vendor-presence gate (<c>05cfe67a</c>).
///
/// Fillz's field shape: an AMD GPU is physically present (vendor detection
/// matches), h264_amf is in ffmpeg's compiled encoder list, yet the real
/// encoder never actually initializes (driver too old / wrong generation).
/// Vendor presence alone is not enough evidence — only a name that survived
/// the real probe may be selected.
/// </summary>
public class PlanStageHardwareEncoderProbeAuthorityTests
{
    private static async Task<OutputPlan> RunPlan(
        IReadOnlyList<GpuDevice> gpus,
        IReadOnlySet<string> usableHardwareEncoders,
        HashSet<string> compiledEncoders
    )
    {
        CodecRegistry registry = new();
        CodecResolver codecResolver = new(registry);
        HardwarePreferenceResolver hardwarePreferenceResolver = new();
        BitDepthPolicyResolver bitDepthPolicyResolver = new();

        Mock<IHardwareCapabilities> hardware = new();
        hardware.Setup(h => h.HasGpu).Returns(gpus.Count > 0);
        hardware.Setup(h => h.CpuCores).Returns(16);
        hardware.Setup(h => h.Gpus).Returns(gpus);
        hardware
            .Setup(h => h.SupportsHardwareEncoding(It.IsAny<VideoCodecType>()))
            .Returns(gpus.Count > 0);
        hardware
            .Setup(h => h.GetGpuForCodec(It.IsAny<VideoCodecType>()))
            .Returns(gpus.Count > 0 ? gpus[0] : null);
        hardware.Setup(h => h.UsableHardwareEncoders).Returns(usableHardwareEncoders);

        Mock<IFfmpegCapabilities> ffmpegCapabilities = new();
        ffmpegCapabilities.Setup(c => c.AvailableEncoders).Returns(compiledEncoders);
        ffmpegCapabilities.Setup(c => c.AvailableFilters).Returns(new HashSet<string>());
        ffmpegCapabilities
            .Setup(c => c.HasEncoder(It.IsAny<string>()))
            .Returns((string encoderName) => compiledEncoders.Contains(encoderName));
        ffmpegCapabilities.Setup(c => c.HasFilter(It.IsAny<string>())).Returns(false);

        // Empty SpeedIndex — no encoder has ever been benchmarked. Forces the
        // resolver's unmeasured-encoder fallback, which is exactly the branch
        // that used to trust ffmpeg's compiled list (plus the vendor gate)
        // without confirming a real init.
        SpeedIndex speedIndex = new(new());

        PlanStage stage = new(
            new(),
            new(),
            new(),
            codecResolver,
            hardware.Object,
            new TonemapSelector(),
            ffmpegCapabilities.Object,
            new AbrLadderGenerator(),
            new NoOpCropDetector(),
            NullLogger<PlanStage>.Instance,
            hardwarePreferenceResolver: hardwarePreferenceResolver,
            speedIndex: speedIndex,
            bitDepthPolicyResolver: bitDepthPolicyResolver
        );

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "Test 1080p H264",
            Container: Container.HlsTs,
            Video: null,
            Audio: [],
            Subtitles: [],
            Ladder: new()
            {
                Mode = LadderMode.Manual,
                Rungs =
                [
                    new LadderRung(
                        Width: 1920,
                        Height: 1080,
                        Codec: VideoCodecType.H264,
                        BitrateKbps: 4000,
                        MaxBitrateKbps: 6000,
                        BufferSizeKbps: 8000,
                        Framerate: 24.0,
                        Preset: "medium",
                        CodecProfile: CodecProfile.High,
                        BitDepth: 8,
                        PixelFormat: "yuv420p"
                    ),
                ],
            }
        );

        MediaInfo media = new(
            FilePath: "/movies/test/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(110),
            OverallBitRateKbps: 12000,
            FileSizeBytes: 9_000_000_000,
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
                    ColorPrimaries: "bt709",
                    ColorTransfer: "bt709",
                    ColorSpace: "bt709",
                    IsDefault: true,
                    // Below the ladder rung's 4000 kbps target so
                    // PlanStage.ApplySmartCopyDowngrade never fires here — this
                    // suite exists to pin hardware-encoder SELECTION, which
                    // smart-copy would bypass entirely (Policy becomes Copy
                    // before any codec/hardware resolution runs).
                    BitRateKbps: 3000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

        ValidateInput input = new(media, profile);
        EncodingContext context = EncodingContext.Create();
        StageResult result = await stage.ExecuteAsync(input, context, CancellationToken.None);

        StageSuccess<ExecutionPlan> success = Assert.IsType<StageSuccess<ExecutionPlan>>(result);
        return success.Value.OutputPlan;
    }

    private static GpuDevice AmdGpu() =>
        new(
            Vendor: GpuVendor.Amd,
            Name: "AMD Radeon RX 6600",
            VramMb: 8192,
            MaxEncoderSessions: 8,
            SupportedCodecs: [VideoCodecType.H264, VideoCodecType.H265]
        );

    private static GpuDevice NvidiaGpu() =>
        new(
            Vendor: GpuVendor.Nvidia,
            Name: "NVIDIA GeForce GTX 1060 3GB",
            VramMb: 3072,
            MaxEncoderSessions: 3,
            SupportedCodecs: [VideoCodecType.H264, VideoCodecType.H265]
        );

    // -------------------------------------------------------------------------
    // (b) A physically detected, vendor-matching GPU is NOT sufficient —
    // only the real init probe result may authorize selection.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task VendorMatchesButProbeFailed_FallsBackToSoftware()
    {
        HashSet<string> compiled = ["libx264", "libx265", "h264_amf", "hevc_amf", "aac"];

        // AMD GPU physically present (vendor gate would have allowed h264_amf
        // under the old 05cfe67a gate) but the real init probe found it
        // unusable — driver too old / wrong RDNA generation / anything a
        // vendor-name match cannot see.
        OutputPlan plan = await RunPlan(
            gpus: [AmdGpu()],
            usableHardwareEncoders: new HashSet<string>(),
            compiledEncoders: compiled
        );

        VideoOutputPlan video = Assert.Single(plan.VideoOutputs);
        video.EncoderName.Should().Be("libx264");
        video.EncoderName.Should().NotBe("h264_amf");
    }

    [Fact]
    public async Task VendorMatchesButProbeFailed_MultiGpuHost_FallsBackToNvencNotAmf()
    {
        HashSet<string> compiled =
        [
            "libx264",
            "libx265",
            "h264_amf",
            "hevc_amf",
            "h264_nvenc",
            "hevc_nvenc",
            "aac",
        ];

        // Both an AMD and an NVIDIA card are physically present. Only nvenc
        // survived the real init probe — h264_amf must never be chosen even
        // though its vendor (AMD) is unambiguously detected.
        OutputPlan plan = await RunPlan(
            gpus: [AmdGpu(), NvidiaGpu()],
            usableHardwareEncoders: new HashSet<string> { "h264_nvenc", "hevc_nvenc" },
            compiledEncoders: compiled
        );

        VideoOutputPlan video = Assert.Single(plan.VideoOutputs);
        video.EncoderName.Should().Be("h264_nvenc");
        video.EncoderName.Should().NotBe("h264_amf");
    }

    // -------------------------------------------------------------------------
    // (c) An empty probe set — even with a GPU physically present — always
    // resolves to software. The probe result is the gate, not GPU presence.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GpuPresentButProbeSetEmpty_ResolvesSoftware()
    {
        HashSet<string> compiled = ["libx264", "libx265", "h264_nvenc", "hevc_nvenc", "aac"];

        OutputPlan plan = await RunPlan(
            gpus: [NvidiaGpu()],
            usableHardwareEncoders: new HashSet<string>(),
            compiledEncoders: compiled
        );

        VideoOutputPlan video = Assert.Single(plan.VideoOutputs);
        video.EncoderName.Should().Be("libx264");
    }

    // -------------------------------------------------------------------------
    // (d) Software encoders are never gated on UsableHardwareEncoders — they
    // are unconditionally selectable regardless of what the probe found.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SoftwareEncoder_AlwaysSelectable_RegardlessOfProbeResult()
    {
        HashSet<string> compiled = ["libx264", "libx265", "aac"];

        OutputPlan plan = await RunPlan(
            gpus: [],
            usableHardwareEncoders: new HashSet<string>(),
            compiledEncoders: compiled
        );

        VideoOutputPlan video = Assert.Single(plan.VideoOutputs);
        video.EncoderName.Should().Be("libx264");
    }
}
