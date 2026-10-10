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
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Optimizer;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.PostProcess;
using NoMercy.Tests.Encoder.Storage;
using SubtitlePolicy = NoMercy.Encoder.Profiles.SubtitlePolicy;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

public class BuildStageBurnInTests
{
    private readonly BuildStage _stage;

    public BuildStageBurnInTests()
    {
        EncoderOptions options = new() { FfmpegPathOverride = "ffmpeg" };
        _stage = new(
            options,
            new FontExtractor(TestStorageFactory.CreateLocal()),
            new SubtitleExtractor(),
            OutputStrategyFactoryTestHelper.Create(),
            [],
            NullLogger<BuildStage>.Instance,
            TestStorageFactory.CreateLocal(),
            new(),
            new()
        );
    }

    [Fact]
    public async Task BurnIn_AddsSubtitlesFilterToVideoChain()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1920, 1080, "[v0]")],
            AudioOutputs: [],
            SubtitleOutputs:
            [
                new(
                    OutputCodec: SubtitleCodecType.Ass,
                    Action: StreamAction.Transcode,
                    Language: "en",
                    SourceIndex: 0,
                    MapLabel: "0:s:0",
                    PlaylistNameTemplate: "subtitles/burn",
                    Policy: SubtitlePolicy.BurnIn
                ),
            ],
            Thumbnails: null
        );

        string filterValue = await GetFilterComplex(outputPlan, "/movies/test.mkv", 1920, 1080);

        // Phase 4.6: ASS source uses the dedicated `ass=` filter (was `subtitles=`).
        Assert.Contains("ass=", filterValue);
    }

    [Fact]
    public async Task BurnIn_EscapesColonsInInputPath()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1920, 1080, "[v0]")],
            AudioOutputs: [],
            SubtitleOutputs:
            [
                new(
                    OutputCodec: SubtitleCodecType.Ass,
                    Action: StreamAction.Transcode,
                    Language: "en",
                    SourceIndex: 0,
                    MapLabel: "0:s:0",
                    Policy: SubtitlePolicy.BurnIn
                ),
            ],
            Thumbnails: null
        );

        string filterValue = await GetFilterComplex(outputPlan, "C:/movies/test.mkv", 1920, 1080);

        Assert.Contains("C\\:/movies/test.mkv", filterValue);
    }

    [Fact]
    public async Task BurnIn_DoesNotEmitSeparateSubtitleOutput()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1920, 1080, "[v0]")],
            AudioOutputs: [],
            SubtitleOutputs:
            [
                new(
                    OutputCodec: SubtitleCodecType.Ass,
                    Action: StreamAction.Transcode,
                    Language: "en",
                    SourceIndex: 0,
                    MapLabel: "0:s:0",
                    Policy: SubtitlePolicy.BurnIn
                ),
            ],
            Thumbnails: null
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfoWithSubtitle(1920, 1080, textBased: true)
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);
        Assert.IsType<StageSuccess<FfmpegCommand[]>>(result);

        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;

        // Count outputs in the main command — should be video only (no subtitle output).
        // -map 0:s:X flag would indicate a subtitle stream output.
        string args = string.Join(" ", commands[0].Arguments);
        Assert.DoesNotContain("-map 0:s:", args);
    }

    [Fact]
    public async Task BurnIn_AppliedAfterScale()
    {
        // When scaling + burn-in: filter chain should be scale → subtitles, not subtitles → scale.
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1280, 720, "[v0]")],
            AudioOutputs: [],
            SubtitleOutputs:
            [
                new(
                    OutputCodec: SubtitleCodecType.Ass,
                    Action: StreamAction.Transcode,
                    Language: "en",
                    SourceIndex: 0,
                    MapLabel: "0:s:0",
                    Policy: SubtitlePolicy.BurnIn
                ),
            ],
            Thumbnails: null
        );

        string filterValue = await GetFilterComplex(outputPlan, "/movies/test.mkv", 1920, 1080);

        int scaleIdx = filterValue.IndexOf("scale=1280", StringComparison.Ordinal);
        int burnIdx = filterValue.IndexOf("ass=", StringComparison.Ordinal);

        Assert.True(scaleIdx >= 0, "scale filter must be present");
        Assert.True(burnIdx >= 0, "ass filter must be present");
        Assert.True(scaleIdx < burnIdx, "burn-in must come after scale in the filter chain");
    }

    [Fact]
    public async Task ExtractMode_DoesNotAddSubtitlesFilter()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1920, 1080, "[v0]")],
            AudioOutputs: [],
            SubtitleOutputs:
            [
                new(
                    OutputCodec: SubtitleCodecType.WebVtt,
                    Action: StreamAction.Extract,
                    Language: "en",
                    SourceIndex: 0,
                    MapLabel: "0:s:0",
                    Policy: SubtitlePolicy.Extract
                ),
            ],
            Thumbnails: null
        );

        string filterValue = await GetFilterComplex(outputPlan, "/movies/test.mkv", 1920, 1080);

        Assert.DoesNotContain("subtitles=", filterValue);
    }

    private async Task<string> GetFilterComplex(
        OutputPlan outputPlan,
        string inputPath,
        int srcWidth,
        int srcHeight
    )
    {
        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, inputPath, "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfoWithSubtitle(srcWidth, srcHeight, textBased: true)
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);
        Assert.IsType<StageSuccess<FfmpegCommand[]>>(result);

        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;
        int idx = Array.IndexOf(commands[0].Arguments, "-filter_complex");
        Assert.True(idx >= 0, "filter_complex flag must be present");
        return commands[0].Arguments[idx + 1];
    }

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

    private static MediaInfo BuildMediaInfoWithSubtitle(int width, int height, bool textBased) =>
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
            SubtitleStreams:
            [
                new(
                    Index: 0,
                    Codec: textBased ? "ass" : "hdmv_pgs_subtitle",
                    Language: "en",
                    IsDefault: true,
                    IsForced: false,
                    Title: null
                ),
            ],
            Chapters: []
        );

    [Fact]
    public async Task AssBurnIn_UsesAssFilterNotSubtitlesFilter()
    {
        // When the source codec is ASS the builder must emit `ass=` not
        // the generic `subtitles=` so libass handles the rendering path.
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1920, 1080, "[v0]")],
            AudioOutputs: [],
            SubtitleOutputs:
            [
                new(
                    OutputCodec: SubtitleCodecType.Ass,
                    Action: StreamAction.Transcode,
                    Language: "en",
                    SourceIndex: 0,
                    MapLabel: "0:s:0",
                    Policy: SubtitlePolicy.BurnIn
                ),
            ],
            Thumbnails: null
        );

        string filterValue = await GetFilterComplexWithCodec(
            outputPlan,
            "/movies/test.mkv",
            1920,
            1080,
            "ass"
        );

        Assert.Contains("ass=", filterValue);
    }

    [Fact]
    public async Task PgsBurnIn_UsesOverlayFilterComplex()
    {
        // PGS burn-in must produce `overlay=format=auto` in -filter_complex,
        // not the text-subtitle `subtitles=` filter.
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1920, 1080, "[v0]")],
            AudioOutputs: [],
            SubtitleOutputs:
            [
                new(
                    OutputCodec: SubtitleCodecType.Ass,
                    Action: StreamAction.Transcode,
                    Language: "en",
                    SourceIndex: 0,
                    MapLabel: "0:s:0",
                    Policy: SubtitlePolicy.BurnIn
                ),
            ],
            Thumbnails: null
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfoWithSubtitle(1920, 1080, textBased: false)
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);
        Assert.IsType<StageSuccess<FfmpegCommand[]>>(result);

        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;
        string args = string.Join(" ", commands[0].Arguments);

        Assert.Contains("overlay=format=auto", args);
    }

    [Fact]
    public async Task BurnIn_EmitsBurnInPermanentDecisionLog()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1920, 1080, "[v0]")],
            AudioOutputs: [],
            SubtitleOutputs:
            [
                new(
                    OutputCodec: SubtitleCodecType.Ass,
                    Action: StreamAction.Transcode,
                    Language: "en",
                    SourceIndex: 0,
                    MapLabel: "0:s:0",
                    Policy: SubtitlePolicy.BurnIn
                ),
            ],
            Thumbnails: null
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        ScopedDecisionLog decisions = new();
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfoWithSubtitle(1920, 1080, textBased: true),
            decisions
        );

        await _stage.ExecuteAsync(input, context, default);

        IReadOnlyList<DecisionLog> snapshot = decisions.Snapshot();
        Assert.Contains(snapshot, d => d.Key == EncoderRuleId.SubtitlesBurnInPermanent);
    }

    [Fact]
    public async Task ExtractMode_DoesNotEmitBurnInDecisionLog()
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs: [BuildVideoOutput(1920, 1080, "[v0]")],
            AudioOutputs: [],
            SubtitleOutputs:
            [
                new(
                    OutputCodec: SubtitleCodecType.WebVtt,
                    Action: StreamAction.Extract,
                    Language: "en",
                    SourceIndex: 0,
                    MapLabel: "0:s:0",
                    Policy: SubtitlePolicy.Extract
                ),
            ],
            Thumbnails: null
        );

        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, "/movies/test.mkv", "/tmp/nmtest-output/test", "Test.NoMercy");
        ScopedDecisionLog decisions = new();
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfoWithSubtitle(1920, 1080, textBased: true),
            decisions
        );

        await _stage.ExecuteAsync(input, context, default);

        IReadOnlyList<DecisionLog> snapshot = decisions.Snapshot();
        Assert.DoesNotContain(snapshot, d => d.Key == EncoderRuleId.SubtitlesBurnInPermanent);
    }

    /// <summary>
    /// Variant of <see cref="GetFilterComplex"/> that lets the caller
    /// specify which subtitle codec the source stream carries, so tests
    /// can distinguish ASS filter dispatch from generic subtitles= dispatch.
    /// </summary>
    private async Task<string> GetFilterComplexWithCodec(
        OutputPlan outputPlan,
        string inputPath,
        int srcWidth,
        int srcHeight,
        string subtitleCodec
    )
    {
        ExecutionPlan plan = BuildPlan(outputPlan);
        BuildInput input = new(plan, inputPath, "/tmp/nmtest-output/test", "Test.NoMercy");
        EncodingContext context = new(
            EncodingContext.Create().CorrelationId,
            BuildMediaInfoWithCodec(srcWidth, srcHeight, subtitleCodec)
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);
        Assert.IsType<StageSuccess<FfmpegCommand[]>>(result);

        FfmpegCommand[] commands = ((StageSuccess<FfmpegCommand[]>)result).Value;
        int idx = Array.IndexOf(commands[0].Arguments, "-filter_complex");
        Assert.True(idx >= 0, "filter_complex flag must be present");
        return commands[0].Arguments[idx + 1];
    }

    private static MediaInfo BuildMediaInfoWithCodec(int width, int height, string codec) =>
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
            SubtitleStreams:
            [
                new(
                    Index: 0,
                    Codec: codec,
                    Language: "en",
                    IsDefault: true,
                    IsForced: false,
                    Title: null
                ),
            ],
            Chapters: []
        );

    private static VideoOutputPlan BuildVideoOutput(int width, int height, string mapLabel) =>
        new(
            Width: width,
            Height: height,
            EncoderName: "libx264",
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
}
