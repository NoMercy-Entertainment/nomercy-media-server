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
using NoMercy.Tests.Encoder.Pipeline.Stages;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Pipeline.Resume;

/// <summary>
/// A resumed encode seeks the main command to the resume point, so a sprite built
/// on that command would only cover the tail. The sprite gets its own command that
/// reads the whole source; an encode that does not resume is built as before.
/// </summary>
public class BuildStageResumedSpriteTests
{
    private const string Source = "/movies/test.mkv";
    private const string OutputDirectory = "/tmp/nmtest-output/test";

    private readonly BuildStage _stage;

    public BuildStageResumedSpriteTests()
    {
        _stage = new(
            new() { FfmpegPathOverride = "ffmpeg" },
            new FontExtractor(TestStorageFactory.CreateLocal()),
            new SubtitleExtractor(),
            OutputStrategyFactoryTestHelper.Create(),
            [],
            NullLogger<BuildStage>.Instance,
            TestStorageFactory.CreateLocal()
        );
    }

    [Fact]
    public async Task ResumedTranscode_MainCommandCarriesNoSprite()
    {
        FfmpegCommand[] commands = await Build(resumeFromMs: 10010);

        FfmpegCommand main = commands[0];
        main.Arguments.Should().Contain("-ss");
        main.Arguments.Should().NotContain("[thumbs]");
        main.Arguments.Should().NotContain("spritevtt");
        main.Arguments.Should().NotContain("thumbs_160x90.webp");
        main.Arguments.Should().NotContain("thumbs_160x90.vtt");
        string filterGraph = main.Arguments[Array.IndexOf(main.Arguments, "-filter_complex") + 1];
        filterGraph.Should().NotContain("thumbs");
    }

    [Fact]
    public async Task ResumedTranscode_SpriteHasItsOwnCommandOverTheWholeSource()
    {
        FfmpegCommand[] commands = await Build(resumeFromMs: 10010);

        FfmpegCommand sprite = commands.Single(c => c.Arguments.Contains("spritevtt"));
        sprite.Should().NotBeSameAs(commands[0]);
        sprite.Arguments.Should().NotContain("-ss");
        sprite.Arguments.Should().NotContain("-start_number");
        sprite.Arguments.Should().NotContain("-output_ts_offset");
        sprite.Arguments.Should().Contain(Source);
        sprite.Arguments.Should().Contain("thumbs_160x90.webp");
        sprite.Arguments.Should().Contain("thumbs_160x90.vtt");
        sprite.Arguments.Should().Contain("-frames:v");
        sprite.Arguments[Array.IndexOf(sprite.Arguments, "-vf") + 1].Should().Contain("fps=1/10");
        sprite.ResumeMarkerPath.Should().BeNull();
    }

    [Fact]
    public async Task ResumedTranscode_OnlyTheMainCommandIsSeeked()
    {
        FfmpegCommand[] commands = await Build(resumeFromMs: 10010);

        commands.Count(c => c.Arguments.Contains("-ss")).Should().Be(1);
        commands[0].Arguments.Should().Contain("-ss");
    }

    [Fact]
    public async Task Unresumed_ArgumentListsAreUnchanged()
    {
        FfmpegCommand[] commands = await Build(resumeFromMs: null);

        commands.Should().ContainSingle();
        Normalize(commands[0].Arguments).Should().Equal(UnresumedSnapshot);
    }

    private async Task<FfmpegCommand[]> Build(long? resumeFromMs)
    {
        OutputPlan outputPlan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    Width: 1920,
                    Height: 1080,
                    EncoderName: "libx264",
                    Crf: 23,
                    BitrateKbps: 4000,
                    Preset: "medium",
                    Profile: "high",
                    Level: "4.1",
                    TenBit: false,
                    PixelFormat: "yuv420p",
                    MapLabel: "[v0]",
                    ExtraFlags: new()
                ),
            ],
            AudioOutputs:
            [
                new(
                    EncoderName: "aac",
                    BitrateKbps: 192,
                    Channels: 2,
                    SampleRate: 48000,
                    Action: StreamAction.Transcode,
                    Language: "en",
                    MapLabel: "0:a:0"
                ),
            ],
            SubtitleOutputs: [],
            Thumbnails: new(160, 90, 10, SpriteGrid.For(TimeSpan.FromHours(2), 10))
        );
        ExecutionPlan plan = new(
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
        MediaInfo mediaInfo = new(
            FilePath: Source,
            Format: "matroska",
            Duration: TimeSpan.FromHours(2),
            OverallBitRateKbps: 8000,
            FileSizeBytes: 7_200_000_000,
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
        BuildInput input = new(
            plan,
            Source,
            OutputDirectory,
            "Test.NoMercy",
            ResumeFromMs: resumeFromMs,
            ExactResume: resumeFromMs is not null
        );

        StageResult result = await _stage.ExecuteAsync(
            input,
            new(EncodingContext.Create().CorrelationId, mediaInfo),
            default
        );

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        return ((StageSuccess<FfmpegCommand[]>)result).Value;
    }

    private static string[] Normalize(string[] arguments) =>
        [.. arguments.Select(a => a.Replace('\\', '/'))];

    // The argument list of an unresumed build, taken before the sprite split existed.
    private static readonly string[] UnresumedSnapshot =
    [
        "-y",
        "-hide_banner",
        "-progress",
        "pipe:1",
        "-i",
        "/movies/test.mkv",
        "-filter_complex",
        "[0:v:0]split=2[split0][thumbsrc];[split0]copy[v0];[thumbsrc]fps=1/10,scale=160:-2,format=yuvj420p,tpad=stop=729:stop_mode=add:color=black[thumbs]",
        "-map",
        "[v0]",
        "-c:v",
        "libx264",
        "-preset",
        "medium",
        "-profile:v",
        "high",
        "-level",
        "4.1",
        "-crf",
        "23",
        "-b:v",
        "4000k",
        "-g",
        "288",
        "-f",
        "hls",
        "-hls_time",
        "6",
        "-hls_playlist_type",
        "vod",
        "-hls_segment_type",
        "mpegts",
        "-hls_flags",
        "independent_segments",
        "-hls_segment_filename",
        "video_1920x1080_SDR/video_1920x1080_SDR_%05d.ts",
        "-force_key_frames",
        "expr:gte(t,n_forced*6)",
        "-forced-idr",
        "1",
        "-map_metadata",
        "-1",
        "-metadata:s:v:0",
        "language=",
        "-metadata:s:v:0",
        "title=",
        "-metadata:s:v:0",
        "handler_name=",
        "video_1920x1080_SDR/video_1920x1080_SDR.m3u8",
        "-map",
        "0:a:0",
        "-c:a",
        "aac",
        "-b:a",
        "192k",
        "-ac",
        "2",
        "-ar",
        "48000",
        "-f",
        "hls",
        "-hls_time",
        "6",
        "-hls_playlist_type",
        "vod",
        "-hls_segment_type",
        "mpegts",
        "-hls_flags",
        "independent_segments",
        "-hls_segment_filename",
        "audio_en_aac/audio_en_aac_%05d.ts",
        "-map_metadata",
        "-1",
        "-metadata:s:a:0",
        "language=en",
        "-metadata:s:a:0",
        "title=English",
        "-metadata:s:a:0",
        "handler_name=English",
        "audio_en_aac/audio_en_aac.m3u8",
        "-map",
        "[thumbs]",
        "-frames:v",
        "729",
        "-f",
        "spritevtt",
        "-sprite_columns",
        "27",
        "-vtt_filename",
        "thumbs_160x90.vtt",
        "thumbs_160x90.webp",
    ];
}
