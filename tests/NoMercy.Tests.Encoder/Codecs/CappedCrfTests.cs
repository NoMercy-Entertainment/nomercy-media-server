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
using NoMercy.Tests.Encoder.Pipeline.Stages;
using NoMercy.Tests.Encoder.Storage;
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using Container = NoMercy.Encoder.Profiles.Container;
using StreamPolicy = NoMercy.Encoder.Profiles.StreamPolicy;
using V2RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;

namespace NoMercy.Tests.Encoder.Codecs;

/// <summary>
/// Regression tests for capped-CRF rate control (Phase 3.10 fix).
///
/// Before the fix, RateControl.CrfCapped was dashboard-display-only — the
/// -maxrate / -bufsize flags were never written into VideoOutputPlan.ExtraFlags,
/// so the bitrate ceiling was silently ignored by FFmpeg.
///
/// These tests verify the fix: PlanStage.BuildOutputPlan injects -maxrate and
/// -bufsize into ExtraFlags whenever both Crf > 0 and BitrateKbps > 0.
/// </summary>
public class CappedCrfTests
{
    // ----------------------------------------------------------------
    // Infrastructure
    // ----------------------------------------------------------------

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

    private static ValidateInput FakeInput(
        VideoCodecType codec,
        int crf,
        int bitrateKbps,
        Container container = Container.HlsTs
    ) =>
        new(
            Media: FakeMediaInfo(),
            Profile: new(
                Id: Ulid.NewUlid(),
                Name: "Test",
                Container: container,
                Video: new(
                    Policy: StreamPolicy.Transcode,
                    Codec: codec,
                    Width: 1920,
                    Height: null,
                    RateControl: V2RateControlMode.Crf,
                    Crf: crf,
                    BitrateKbps: bitrateKbps,
                    MaxBitrateKbps: null,
                    BufferSizeKbps: null,
                    Preset: "fast",
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

    private static ResolvedCodec SoftwareCodec(
        string ffmpegName,
        RateControlMode defaultRc = RateControlMode.Crf,
        int qualityMax = 51
    ) =>
        new(
            FfmpegEncoderName: ffmpegName,
            EncoderInfo: new(
                FfmpegName: ffmpegName,
                RequiredVendor: null,
                Presets: ["slow", "medium", "fast"],
                Profiles: ["high"],
                Levels: ["4.1"],
                QualityRange: new(0, qualityMax, qualityMax / 2),
                SupportedRateControl: [RateControlMode.Crf, RateControlMode.Cbr],
                Supports10Bit: false,
                SupportsHdr: false,
                MaxConcurrentSessions: int.MaxValue,
                PixelFormat10Bit: "yuv420p10le",
                VendorSpecificFlags: new()
            ),
            Device: null,
            DefaultRateControl: defaultRc
        );

    private static ResolvedCodec HardwareCodec(
        string ffmpegName,
        RateControlMode defaultRc,
        int qualityMax = 51
    ) =>
        new(
            FfmpegEncoderName: ffmpegName,
            EncoderInfo: new(
                FfmpegName: ffmpegName,
                RequiredVendor: null,
                Presets: ["p1", "p4", "p7"],
                Profiles: ["high"],
                Levels: ["4.1"],
                QualityRange: new(0, qualityMax, qualityMax / 2),
                SupportedRateControl: [defaultRc],
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
                SupportedCodecs: [VideoCodecType.H264, VideoCodecType.H265]
            ),
            DefaultRateControl: defaultRc
        );

    private static async Task<VideoOutputPlan> RunPlanAsync(PlanStage stage, ValidateInput input)
    {
        EncodingContext ctx = EncodingContext.Create();
        StageResult result = await stage.ExecuteAsync(input, ctx, CancellationToken.None);
        ExecutionPlan plan = ((StageSuccess<ExecutionPlan>)result).Value;
        return plan.OutputPlan.VideoOutputs[0];
    }

    // ----------------------------------------------------------------
    // libx264 — software, -crf + -maxrate + -bufsize
    // ----------------------------------------------------------------

    [Fact]
    public async Task CrfCapped_emits_crf_maxrate_bufsize_for_libx264()
    {
        PlanStage stage = BuildPlanStage(SoftwareCodec("libx264"));
        VideoOutputPlan video = await RunPlanAsync(
            stage,
            FakeInput(VideoCodecType.H264, crf: 22, bitrateKbps: 4000)
        );

        // libx264: quality flows through VideoOutputPlan.Crf → -crf; ceiling via ExtraFlags.
        video.Crf.Should().Be(22);
        video.ExtraFlags.Should().ContainKey("-maxrate").WhoseValue.Should().Be("4000k");
        video.ExtraFlags.Should().ContainKey("-bufsize").WhoseValue.Should().Be("8000k");
        // The ceiling is the VBV cap (-maxrate/-bufsize), never a target bitrate:
        // BitrateKbps must be cleared so no -b:v collides with -crf and forces ABR.
        video.BitrateKbps.Should().Be(0);
    }

    // ----------------------------------------------------------------
    // libx265 — software, same flag names as libx264
    // ----------------------------------------------------------------

    [Fact]
    public async Task CrfCapped_emits_crf_maxrate_bufsize_for_libx265()
    {
        PlanStage stage = BuildPlanStage(SoftwareCodec("libx265"));
        VideoOutputPlan video = await RunPlanAsync(
            stage,
            FakeInput(VideoCodecType.H265, crf: 24, bitrateKbps: 3000)
        );

        video.Crf.Should().Be(24);
        video.ExtraFlags.Should().ContainKey("-maxrate").WhoseValue.Should().Be("3000k");
        video.ExtraFlags.Should().ContainKey("-bufsize").WhoseValue.Should().Be("6000k");
    }

    // ----------------------------------------------------------------
    // h264_nvenc — hardware, quality via -cq (in ExtraFlags), ceiling via -maxrate / -bufsize
    // ----------------------------------------------------------------

    [Fact]
    public async Task CrfCapped_emits_cq_maxrate_bufsize_for_h264_nvenc()
    {
        PlanStage stage = BuildPlanStage(HardwareCodec("h264_nvenc", RateControlMode.Cq));
        VideoOutputPlan video = await RunPlanAsync(
            stage,
            FakeInput(VideoCodecType.H264, crf: 22, bitrateKbps: 4000)
        );

        // NVENC: quality mapped to -cq in ExtraFlags, not -crf.
        video.ExtraFlags.Should().ContainKey("-cq");
        video.ExtraFlags.Should().ContainKey("-maxrate").WhoseValue.Should().Be("4000k");
        video.ExtraFlags.Should().ContainKey("-bufsize").WhoseValue.Should().Be("8000k");
    }

    // ----------------------------------------------------------------
    // hevc_nvenc — hardware, same NVENC path as h264_nvenc
    // ----------------------------------------------------------------

    [Fact]
    public async Task CrfCapped_emits_cq_maxrate_bufsize_for_hevc_nvenc()
    {
        PlanStage stage = BuildPlanStage(HardwareCodec("hevc_nvenc", RateControlMode.Cq));
        VideoOutputPlan video = await RunPlanAsync(
            stage,
            FakeInput(VideoCodecType.H265, crf: 26, bitrateKbps: 2000)
        );

        video.ExtraFlags.Should().ContainKey("-cq");
        video.ExtraFlags.Should().ContainKey("-maxrate").WhoseValue.Should().Be("2000k");
        video.ExtraFlags.Should().ContainKey("-bufsize").WhoseValue.Should().Be("4000k");
    }

    // ----------------------------------------------------------------
    // h264_qsv — hardware, quality via -global_quality (ICQ mode)
    // ----------------------------------------------------------------

    [Fact]
    public async Task CrfCapped_emits_global_quality_maxrate_bufsize_for_h264_qsv()
    {
        PlanStage stage = BuildPlanStage(HardwareCodec("h264_qsv", RateControlMode.Icq));
        VideoOutputPlan video = await RunPlanAsync(
            stage,
            FakeInput(VideoCodecType.H264, crf: 22, bitrateKbps: 5000)
        );

        video.ExtraFlags.Should().ContainKey("-global_quality");
        video.ExtraFlags.Should().ContainKey("-maxrate").WhoseValue.Should().Be("5000k");
        video.ExtraFlags.Should().ContainKey("-bufsize").WhoseValue.Should().Be("10000k");
    }

    // ----------------------------------------------------------------
    // h264_videotoolbox — hardware, quality via -q:v
    // ----------------------------------------------------------------

    [Fact]
    public async Task CrfCapped_emits_qv_maxrate_bufsize_for_h264_videotoolbox()
    {
        PlanStage stage = BuildPlanStage(
            HardwareCodec("h264_videotoolbox", RateControlMode.QualityLevel, qualityMax: 100)
        );
        VideoOutputPlan video = await RunPlanAsync(
            stage,
            FakeInput(VideoCodecType.H264, crf: 22, bitrateKbps: 3500)
        );

        video.ExtraFlags.Should().ContainKey("-q:v");
        video.ExtraFlags.Should().ContainKey("-maxrate").WhoseValue.Should().Be("3500k");
        video.ExtraFlags.Should().ContainKey("-bufsize").WhoseValue.Should().Be("7000k");
    }

    // ----------------------------------------------------------------
    // Boundary: pure CRF (no bitrate) — must NOT emit -maxrate / -bufsize
    // ----------------------------------------------------------------

    [Fact]
    public async Task PureCrf_does_not_emit_maxrate_or_bufsize()
    {
        PlanStage stage = BuildPlanStage(SoftwareCodec("libx264"));
        // bitrateKbps = 0 → pure CRF, no ceiling.
        VideoOutputPlan video = await RunPlanAsync(
            stage,
            FakeInput(VideoCodecType.H264, crf: 22, bitrateKbps: 0)
        );

        video.ExtraFlags.Should().NotContainKey("-maxrate");
        video.ExtraFlags.Should().NotContainKey("-bufsize");
    }

    // ----------------------------------------------------------------
    // Boundary: pure bitrate (no CRF) — must NOT emit -maxrate / -bufsize
    // (ABR mode; ceiling is not the server's job here)
    // ----------------------------------------------------------------

    [Fact]
    public async Task PureBitrate_does_not_emit_maxrate_or_bufsize()
    {
        PlanStage stage = BuildPlanStage(SoftwareCodec("libx264"));
        // crf = 0 → pure ABR; -maxrate / -bufsize are not injected by the capped-CRF path.
        VideoOutputPlan video = await RunPlanAsync(
            stage,
            FakeInput(VideoCodecType.H264, crf: 0, bitrateKbps: 4000)
        );

        video.ExtraFlags.Should().NotContainKey("-maxrate");
        video.ExtraFlags.Should().NotContainKey("-bufsize");
    }

    // ----------------------------------------------------------------
    // Boundary: bufsize invariant — always 2× maxrate
    // ----------------------------------------------------------------

    [Fact]
    public async Task CrfCapped_bufsize_is_double_maxrate()
    {
        PlanStage stage = BuildPlanStage(SoftwareCodec("libx264"));
        VideoOutputPlan video = await RunPlanAsync(
            stage,
            FakeInput(VideoCodecType.H264, crf: 22, bitrateKbps: 6000)
        );

        string maxrate = video.ExtraFlags["-maxrate"]; // "6000k"
        string bufsize = video.ExtraFlags["-bufsize"]; // "12000k"

        int maxKbps = int.Parse(maxrate.TrimEnd('k'));
        int bufKbps = int.Parse(bufsize.TrimEnd('k'));

        bufKbps.Should().Be(maxKbps * 2);
    }

    // ----------------------------------------------------------------
    // argv-level: build the REAL ffmpeg command from the plan and assert
    // the capped-CRF output never carries a target bitrate (-b:v). The
    // plan-field tests above cannot see the -crf + -b:v collision because
    // they never serialize the command; these do.
    // ----------------------------------------------------------------

    private static async Task<string> BuildArgvAsync(PlanStage stage, ValidateInput input)
    {
        EncodingContext ctx = EncodingContext.Create();
        StageResult result = await stage.ExecuteAsync(input, ctx, CancellationToken.None);
        ExecutionPlan plan = ((StageSuccess<ExecutionPlan>)result).Value;

        HlsOutputStrategy strategy = new(TestStorageFactory.CreateLocal());
        FfmpegCommandBuilder builder = new();
        builder.AddInput(new("/input.mkv"));
        strategy.ConfigureOutput(builder, plan.OutputPlan, "/output");

        return string.Join(" ", builder.Build("ffmpeg").Arguments);
    }

    [Fact]
    public async Task CrfCapped_libx264_argv_has_crf_and_maxrate_but_no_target_bitrate()
    {
        PlanStage stage = BuildPlanStage(SoftwareCodec("libx264"));
        string argv = await BuildArgvAsync(
            stage,
            FakeInput(VideoCodecType.H264, crf: 22, bitrateKbps: 4000)
        );

        argv.Should().Contain("-crf 22");
        argv.Should().Contain("-maxrate 4000k");
        argv.Should().Contain("-bufsize 8000k");
        // The regression: -b:v alongside -crf makes libx264 switch to ABR and
        // silently voids the CRF quality target. It must never appear.
        argv.Should().NotContain("-b:v");
    }

    [Fact]
    public async Task CrfCapped_nvenc_argv_has_cq_and_maxrate_but_no_target_bitrate()
    {
        PlanStage stage = BuildPlanStage(HardwareCodec("h264_nvenc", RateControlMode.Cq));
        string argv = await BuildArgvAsync(
            stage,
            FakeInput(VideoCodecType.H264, crf: 22, bitrateKbps: 4000)
        );

        argv.Should().Contain("-cq");
        argv.Should().Contain("-maxrate 4000k");
        // NVENC capped-quality must not carry a redundant -b:v target.
        argv.Should().NotContain("-b:v");
    }

    [Fact]
    public async Task PureBitrate_argv_still_emits_target_bitrate()
    {
        // Guard the other direction: a pure-ABR profile (no CRF) MUST still
        // emit -b:v — the capped-CRF suppression must not swallow real ABR.
        PlanStage stage = BuildPlanStage(SoftwareCodec("libx264"));
        string argv = await BuildArgvAsync(
            stage,
            FakeInput(VideoCodecType.H264, crf: 0, bitrateKbps: 4000)
        );

        argv.Should().Contain("-b:v 4000k");
        argv.Should().NotContain("-maxrate");
    }
}
