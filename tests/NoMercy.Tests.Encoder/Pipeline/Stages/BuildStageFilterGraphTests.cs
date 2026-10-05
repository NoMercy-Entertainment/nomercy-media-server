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
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Optimizer;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.PostProcess;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

public class BuildStageFilterGraphTests
{
    private readonly BuildStage _stage;

    public BuildStageFilterGraphTests()
    {
        EncoderOptions options = new() { FfmpegPathOverride = "ffmpeg" };
        _stage = new(
            options,
            new FontExtractor(TestStorageFactory.CreateLocal()),
            new SubtitleExtractor(),
            OutputStrategyFactoryTestHelper.Create(),
            [],
            NullLogger<BuildStage>.Instance,
            TestStorageFactory.CreateLocal()
        );
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private static ExecutionPlan BuildPlan(OutputPlan outputPlan) =>
        new(
            Groups:
            [
                new(
                    GroupId: "group_0",
                    Nodes: [new("decode_0", OperationType.Decode, [], new())],
                    DeviceId: null,
                    GpuSlotsRequired: 0,
                    CpuThreadsRequired: 4,
                    RequiresGpu: false,
                    Priority: 1
                ),
            ],
            EstimatedTotalDuration: TimeSpan.FromMinutes(90),
            OutputPlan: outputPlan
        );

    private static MediaInfo BuildMediaInfo(int width, int height) =>
        new(
            FilePath: "/movies/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromHours(2),
            OverallBitRateKbps: 8000,
            FileSizeBytes: 7_200_000_000,
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
                    BitRateKbps: 6000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static VideoOutputPlan BuildVideoOutput(
        int width,
        int height,
        string mapLabel,
        string encoder = "libx264"
    ) =>
        new(
            Width: width,
            Height: height,
            EncoderName: encoder,
            Crf: 23,
            BitrateKbps: 4000,
            Preset: "medium",
            Profile: "high",
            Level: "4.1",
            TenBit: false,
            PixelFormat: "yuv420p",
            MapLabel: mapLabel,
            ExtraFlags: new()
        );

    private static AudioOutputPlan BuildAudioOutput() =>
        new(
            EncoderName: "aac",
            BitrateKbps: 192,
            Channels: 2,
            SampleRate: 48000,
            Action: StreamAction.Transcode,
            Language: "en",
            MapLabel: "0:a:0"
        );

    // ------------------------------------------------------------------
    // Test 1: single video same resolution → copy filter
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildStage_SingleVideoSameResolution_GeneratesCopyFilter()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(320, 180, "[v0]")],
            AudioOutputs: [BuildAudioOutput()],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfo(320, 180)
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;

        int filterComplexIdx = Array.IndexOf(commands[0].Arguments, "-filter_complex");
        filterComplexIdx.Should().BeGreaterThan(-1, "a -filter_complex argument must be present");

        string filterValue = commands[0].Arguments[filterComplexIdx + 1];
        filterValue.Should().Contain("copy");
    }

    // ------------------------------------------------------------------
    // Test 2: single video with scaling → scale filter
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildStage_SingleVideoWithScaling_GeneratesScaleFilter()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1280, 720, "[v0]")],
            AudioOutputs: [BuildAudioOutput()],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfo(1920, 1080)
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;

        int filterComplexIdx = Array.IndexOf(commands[0].Arguments, "-filter_complex");
        filterComplexIdx.Should().BeGreaterThan(-1, "a -filter_complex argument must be present");

        string filterValue = commands[0].Arguments[filterComplexIdx + 1];
        filterValue.Should().Contain("scale=1280:-2");
    }

    // ------------------------------------------------------------------
    // Test 3: multiple video outputs → split + scale/copy
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildStage_MultipleVideos_GeneratesSplitAndScale()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                BuildVideoOutput(1920, 1080, "[v0]"),
                BuildVideoOutput(1280, 720, "[v1]"),
            ],
            AudioOutputs: [BuildAudioOutput()],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfo(1920, 1080)
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;

        int filterComplexIdx = Array.IndexOf(commands[0].Arguments, "-filter_complex");
        filterComplexIdx.Should().BeGreaterThan(-1, "a -filter_complex argument must be present");

        string filterValue = commands[0].Arguments[filterComplexIdx + 1];
        filterValue.Should().Contain("split=2");
        filterValue.Should().Contain("copy");
        filterValue.Should().Contain("scale=1280:-2");
    }

    // ------------------------------------------------------------------
    // Test 4: audio-only profile → no filter_complex in command args
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildStage_AudioOnlyProfile_NoFilterComplex()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [],
            AudioOutputs: [BuildAudioOutput()],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfo(1920, 1080)
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;

        commands[0].Arguments.Should().NotContain("-filter_complex");
    }

    // ------------------------------------------------------------------
    // Test 7: GPU-resident plan → -hwaccel decode + GPU scale, no CPU scale
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildStage_GpuResidentPlan_EmitsHwaccelDecodeAndGpuScale()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1280, 720, "[v0]", "h264_nvenc")],
            AudioOutputs: [BuildAudioOutput()],
            SubtitleOutputs: [],
            Thumbnails: null,
            GpuAccel: new("cuda", "cuda", "scale_cuda")
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfo(1920, 1080)
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        string[] args = ((StageSuccess<FfmpegCommand[]>)result).Value[0].Arguments;

        int hwaccelIdx = Array.IndexOf(args, "-hwaccel");
        hwaccelIdx.Should().BeGreaterThan(-1, "decode must be offloaded to the GPU");
        args[hwaccelIdx + 1].Should().Be("cuda");
        int outFmtIdx = Array.IndexOf(args, "-hwaccel_output_format");
        outFmtIdx.Should().BeGreaterThan(-1);
        args[outFmtIdx + 1].Should().Be("cuda", "frames stay in GPU memory");

        string filterValue = args[Array.IndexOf(args, "-filter_complex") + 1];
        filterValue.Should().Contain("scale_cuda=1280:720", "scaling runs on the GPU");
        filterValue.Should().NotContain("scale=1280:-2", "no CPU scale on the GPU-resident path");
    }

    // ------------------------------------------------------------------
    // Test 6: HDR source, one SDR rung + thumbnails → tonemap exactly once
    // (single full-res SDR intermediate feeds both the rung and the sprite)
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildStage_HdrSingleSdrRungWithThumbnails_TonemapsExactlyOnce()
    {
        const string tonemapChain =
            "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,"
            + "tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

        VideoOutputPlan sdrRung = BuildVideoOutput(1920, 1080, "[v0]") with
        {
            ConvertHdrToSdr = true,
            TonemapFilterChain = tonemapChain,
        };

        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [sdrRung],
            AudioOutputs: [BuildAudioOutput()],
            SubtitleOutputs: [],
            Thumbnails: new(320, 180, 10, SpriteGrid.For(TimeSpan.FromHours(2), 10))
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(EncodingContext.Create().CorrelationId, BuildHdrMediaInfo());

        StageResult result = await _stage.ExecuteAsync(input, context, default);

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;

        int filterComplexIdx = Array.IndexOf(commands[0].Arguments, "-filter_complex");
        string filterValue = commands[0].Arguments[filterComplexIdx + 1];

        int tonemapCount = CountOccurrences(filterValue, "tonemap=hable");
        tonemapCount
            .Should()
            .Be(1, "the rung and the sprite must share one full-res SDR intermediate");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static MediaInfo BuildHdrMediaInfo() =>
        new(
            FilePath: "/movies/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromHours(2),
            OverallBitRateKbps: 50000,
            FileSizeBytes: 30_000_000_000,
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
                    BitRateKbps: 45000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    // ------------------------------------------------------------------
    // Test 5: video with CropFilter → crop=... filter emitted
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildStage_CropFilterSet_EmitsCropFilterBeforeScale()
    {
        VideoOutputPlan videoWithCrop = BuildVideoOutput(1280, 720, "[v0]") with
        {
            CropFilter = "1920:800:0:140",
        };

        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [videoWithCrop],
            AudioOutputs: [BuildAudioOutput()],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfo(1920, 1080)
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;

        int filterComplexIdx = Array.IndexOf(commands[0].Arguments, "-filter_complex");
        string filterValue = commands[0].Arguments[filterComplexIdx + 1];

        filterValue.Should().Contain("crop=1920:800:0:140");

        // Crop must appear before scale so the scaler operates on the cropped picture.
        int cropIdx = filterValue.IndexOf("crop=", StringComparison.Ordinal);
        int scaleIdx = filterValue.IndexOf("scale=", StringComparison.Ordinal);
        cropIdx.Should().BeLessThan(scaleIdx);
    }

    // ------------------------------------------------------------------
    // Test 8: shared crop across a 4K-HDR rung + a 1080p-SDR (tonemapped)
    // rung + thumbnails is hoisted to ONE crop on the decoded source, run
    // before the split/tonemap, so the cropped picture feeds every consumer
    // (the HDR rung, the tonemapped SDR rung, AND the sprite) exactly once.
    // This is the "crop the 4K, reuse the cropped frame for the 1080p SDR and
    // the sprite" design — the sprite must never carry the black bars.
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildStage_SharedCropAcrossRungs_HoistsCropOnceBeforeTonemapAndSplit()
    {
        const string tonemapChain =
            "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,"
            + "tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";
        const string crop = "3616:1608:224:276";

        VideoOutputPlan hdrRung = BuildVideoOutput(3840, 2160, "[v0]", "hevc_nvenc") with
        {
            TenBit = true,
            PixelFormat = "p010le",
            CropFilter = crop,
        };
        VideoOutputPlan sdrRung = BuildVideoOutput(1920, 1080, "[v1]", "hevc_nvenc") with
        {
            ConvertHdrToSdr = true,
            TonemapFilterChain = tonemapChain,
            CropFilter = crop,
        };

        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [hdrRung, sdrRung],
            AudioOutputs: [BuildAudioOutput()],
            SubtitleOutputs: [],
            Thumbnails: new(320, 180, 10, SpriteGrid.For(TimeSpan.FromHours(2), 10))
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(EncodingContext.Create().CorrelationId, BuildHdrMediaInfo());

        StageResult result = await _stage.ExecuteAsync(input, context, default);

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;

        int filterComplexIdx = Array.IndexOf(commands[0].Arguments, "-filter_complex");
        string filterValue = commands[0].Arguments[filterComplexIdx + 1];

        // Exactly one crop for the whole graph — not one per rung, not one per branch.
        CountOccurrences(filterValue, $"crop={crop}")
            .Should()
            .Be(1, "the source is cropped once and every branch reuses the cropped picture");

        // The single crop runs before the tonemap and before the split, so the
        // expensive tonemap and the sprite both operate on the cropped picture.
        int cropIdx = filterValue.IndexOf("crop=", StringComparison.Ordinal);
        int tonemapIdx = filterValue.IndexOf("tonemap=hable", StringComparison.Ordinal);
        int splitIdx = filterValue.IndexOf("split", StringComparison.Ordinal);
        cropIdx.Should().BeGreaterThan(-1);
        cropIdx.Should().BeLessThan(tonemapIdx, "tonemap must run on the cropped picture");
        cropIdx.Should().BeLessThan(splitIdx, "the crop feeds the split, not the other way round");

        // Tonemap still happens exactly once (shared SDR intermediate for the
        // 1080p rung and the sprite).
        CountOccurrences(filterValue, "tonemap=hable").Should().Be(1);
    }
}
