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
using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Optimizer;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.PostProcess;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

/// <summary>
/// Which ffmpeg run pays for the attachment dump. A source read of a multi-GB
/// file is the expensive part, so the dump must ride on a run that is happening
/// anyway: the dedicated extraction command when bitmap subtitles give it a
/// reason to exist, and the encode's own read when they do not. The failure
/// these guard against is an "-i source -f null -" command whose only product
/// is fonts — a second full read for a pure data transfer.
/// </summary>
public class BuildStageAttachmentDumpTests
{
    private const string InputPath = "/movies/test.mkv";

    private readonly BuildStage _stage;
    private readonly string _outputDirectory;

    public BuildStageAttachmentDumpTests()
    {
        EncoderOptions options = new()
        {
            FfmpegPathOverride = "ffmpeg",
            FfprobePathOverride = "ffprobe",
        };
        _stage = new(
            options,
            new FontExtractor(TestStorageFactory.CreateLocal()),
            new SubtitleExtractor(),
            OutputStrategyFactoryTestHelper.Create(),
            [],
            NullLogger<BuildStage>.Instance,
            TestStorageFactory.CreateLocal()
        );
        _outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"BuildStageAttachmentDumpTests_{Guid.NewGuid():N}"
        );
    }

    // ------------------------------------------------------------------
    // Text/ASS subtitles are pulled out by the dedicated extraction command
    // (never beside the encode outputs), so that command exists anyway and
    // the dump rides on it. This is the anime case.
    // ------------------------------------------------------------------

    [Fact]
    public async Task TextSubtitlesWithAttachments_DumpRidesOnTheExtractionCommand()
    {
        FfmpegCommand[] commands = await BuildAsync(TextSubtitlePlan(), TextSubtitleStream());

        FfmpegCommand extraction = commands[^1];
        extraction.Arguments.Should().Contain("-dump_attachment:3");
        extraction.Arguments.Should().Contain("-dump_attachment:4");

        commands[0]
            .Arguments.Should()
            .NotContain(a => a.StartsWith("-dump_attachment", StringComparison.Ordinal));

        // No command exists whose only output is the null sink — that shape is
        // the pointless second source read this replaced.
        commands
            .Should()
            .NotContain(command =>
                command.Arguments.Contains("null") && command.Arguments.Contains("-")
            );
    }

    // A text subtitle output beside encode outputs cuts them short in ffmpeg.

    [Fact]
    public async Task TextSubtitles_NeverShareACommandWithEncodeOutputs()
    {
        FfmpegCommand[] commands = await BuildAsync(TextSubtitlePlan(), TextSubtitleStream());

        commands[0].Arguments.Should().NotContain("webvtt");
        commands[0]
            .Arguments.Should()
            .NotContain(a =>
                a.EndsWith(".vtt", StringComparison.Ordinal) && a.Contains("subtitles")
            );

        FfmpegCommand extraction = commands[^1];
        extraction.Arguments.Should().Contain("webvtt");
        extraction.Arguments.Should().Contain("0:s:0");
        extraction.Arguments.Should().NotContain("libx264");
        extraction.Arguments.Count(a => a == "-i").Should().Be(1);
    }

    [Fact]
    public async Task AssSubtitles_NeverShareACommandWithEncodeOutputs()
    {
        FfmpegCommand[] commands = await BuildAsync(
            AssSubtitlePlan(),
            AssSubtitleStream(),
            attachments: []
        );

        commands[0].Arguments.Should().NotContain("ass");
        commands[^1].Arguments.Should().Contain("-c:s");
        commands[^1].Arguments.Should().Contain("ass");
    }

    [Fact]
    public async Task SubtitleTask_ProducesTheTextSubtitlesExactlyOnce_AsTheOnlyCommand()
    {
        FfmpegCommand[] commands = await BuildAsync(
            TextSubtitlePlan(),
            TextSubtitleStream(),
            attachments: [],
            taskFilter: Task(EncodeTaskKind.Subtitle)
        );

        commands.Should().ContainSingle();
        commands[0].Arguments.Count(a => a == "webvtt").Should().Be(1);
        commands[0].Arguments.Should().NotContain("libx264");
    }

    [Fact]
    public async Task VideoTask_CarriesNoTextSubtitleOutputs()
    {
        FfmpegCommand[] commands = await BuildAsync(
            TextSubtitlePlan(),
            TextSubtitleStream(),
            attachments: [],
            taskFilter: Task(EncodeTaskKind.Video)
        );

        commands.Should().ContainSingle();
        commands[0].Arguments.Should().NotContain("webvtt");
    }

    [Fact]
    public async Task BundleWithoutSubtitleSlice_CarriesNoTextSubtitleOutputs()
    {
        FfmpegCommand[] commands = await BuildAsync(
            TextSubtitlePlan(),
            TextSubtitleStream(),
            attachments: [],
            taskFilter: Task(EncodeTaskKind.Whole) with
            {
                SubtitleSliceIndexes = [],
            }
        );

        commands.Should().ContainSingle();
        commands[0].Arguments.Should().NotContain("webvtt");
    }

    [Fact]
    public async Task ExtractionCommand_HonorsDurationLimit()
    {
        FfmpegCommand[] commands = await BuildAsync(
            TextSubtitlePlan(),
            TextSubtitleStream(),
            attachments: [],
            durationLimit: TimeSpan.FromSeconds(30)
        );

        List<string> args = [.. commands[^1].Arguments];
        int limit = args.IndexOf("-t");
        limit.Should().BeGreaterThanOrEqualTo(0);
        limit.Should().BeLessThan(args.IndexOf("-i"));
        args[limit + 1].Should().Be("30.000");
    }

    [Fact]
    public async Task ExtractionCommand_IgnoresResumeSeek()
    {
        FfmpegCommand[] commands = await BuildAsync(
            TextSubtitlePlan(),
            TextSubtitleStream(),
            attachments: [],
            resumeFromMs: 600_000
        );

        commands[0].Arguments.Should().Contain("-ss");
        commands[^1].Arguments.Should().NotContain("-ss");
    }

    [Fact]
    public async Task TextSubtitlesWithAttachments_DumpFlagsPrecedeTheInput()
    {
        FfmpegCommand[] commands = await BuildAsync(TextSubtitlePlan(), TextSubtitleStream());

        List<string> args = [.. commands[^1].Arguments];

        int dumpFlag = args.IndexOf("-dump_attachment:3");
        int inputFlag = args.IndexOf("-i");

        // Assert presence separately — IndexOf returns -1 for a missing flag,
        // which would satisfy the ordering assertion without the flag existing.
        dumpFlag.Should().BeGreaterThanOrEqualTo(0);
        inputFlag.Should().BeGreaterThanOrEqualTo(0);

        // -dump_attachment is an input option: after "-i" it binds to the wrong
        // input, or to none, and ffmpeg writes no attachment at all.
        dumpFlag.Should().BeLessThan(inputFlag);
    }

    // ------------------------------------------------------------------
    // Bitmap subtitles need their own command regardless, so the dump stays
    // there — an extraction failure must not be able to sink the encode when
    // there is a cheaper place to put it.
    // ------------------------------------------------------------------

    [Fact]
    public async Task BitmapSubtitlesWithAttachments_DumpStaysOnTheExtractionCommand()
    {
        FfmpegCommand[] commands = await BuildAsync(BitmapSubtitlePlan(), BitmapSubtitleStream());

        commands.Should().HaveCountGreaterThan(1);
        commands[0]
            .Arguments.Should()
            .NotContain(a => a.StartsWith("-dump_attachment", StringComparison.Ordinal));

        FfmpegCommand extraction = commands[^1];
        extraction.Arguments.Should().Contain("-dump_attachment:3");
        extraction.Arguments.Should().Contain(a => a.EndsWith(".mks", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // A source with no attachments must never gain dump flags — the merge is
    // conditional, not unconditional.
    // ------------------------------------------------------------------

    [Fact]
    public async Task NoAttachments_EncodeCommandCarriesNoDumpFlags()
    {
        FfmpegCommand[] commands = await BuildAsync(
            TextSubtitlePlan(),
            TextSubtitleStream(),
            attachments: []
        );

        commands
            .Should()
            .AllSatisfy(command =>
                command
                    .Arguments.Should()
                    .NotContain(a => a.StartsWith("-dump_attachment", StringComparison.Ordinal))
            );
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<FfmpegCommand[]> BuildAsync(
        SubtitleOutputPlan subtitlePlan,
        SubtitleStreamInfo subtitleStream,
        IReadOnlyList<AttachmentInfo>? attachments = null,
        DecomposedTask? taskFilter = null,
        TimeSpan? durationLimit = null,
        long? resumeFromMs = null
    )
    {
        ExecutionPlan plan = MakePlan(subtitlePlan);
        EncodingContext context = EncodingContext.Create() with
        {
            MediaInfo = MakeMediaInfo(
                subtitleStream,
                attachments
                    ??
                    [
                        new(Index: 3, Codec: "ttf", Filename: "Arial.ttf", MimeType: null),
                        new(Index: 4, Codec: "ttf", Filename: "Comic Sans.ttf", MimeType: null),
                    ]
            ),
            InputPath = InputPath,
        };

        BuildInput input = new(
            plan,
            InputPath,
            _outputDirectory,
            "Test.NoMercy",
            DurationLimit: durationLimit,
            TaskFilter: taskFilter,
            ResumeFromMs: resumeFromMs
        );

        StageResult result = await _stage.ExecuteAsync(input, context, default);

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        return ((StageSuccess<FfmpegCommand[]>)result).Value;
    }

    private static SubtitleOutputPlan TextSubtitlePlan() =>
        new(
            OutputCodec: SubtitleCodecType.WebVtt,
            Action: StreamAction.Extract,
            Language: "en",
            SourceIndex: 0,
            MapLabel: "0:s:0"
        );

    private static DecomposedTask Task(EncodeTaskKind kind) =>
        new(
            TaskId: "t0",
            ParentJobId: 0,
            GroupTag: "g",
            Kind: kind,
            OutputIndex: 0,
            Resources: null
        );

    private static SubtitleOutputPlan AssSubtitlePlan() =>
        new(
            OutputCodec: SubtitleCodecType.Copy,
            Action: StreamAction.Extract,
            Language: "en",
            SourceIndex: 0,
            MapLabel: "0:s:0"
        );

    private static SubtitleStreamInfo AssSubtitleStream() =>
        new(Index: 0, Codec: "ass", Language: "en", IsDefault: true, IsForced: false, Title: null);

    private static SubtitleOutputPlan BitmapSubtitlePlan() =>
        new(
            OutputCodec: SubtitleCodecType.Copy,
            Action: StreamAction.Extract,
            Language: "en",
            SourceIndex: 0,
            MapLabel: "0:s:0"
        );

    private static SubtitleStreamInfo TextSubtitleStream() =>
        new(
            Index: 0,
            Codec: "subrip",
            Language: "en",
            IsDefault: true,
            IsForced: false,
            Title: null
        );

    private static SubtitleStreamInfo BitmapSubtitleStream() =>
        new(
            Index: 0,
            Codec: "hdmv_pgs_subtitle",
            Language: "en",
            IsDefault: true,
            IsForced: false,
            Title: null
        );

    private static MediaInfo MakeMediaInfo(
        SubtitleStreamInfo subtitleStream,
        IReadOnlyList<AttachmentInfo> attachments
    ) =>
        new(
            FilePath: InputPath,
            Format: "matroska",
            Duration: TimeSpan.FromHours(2),
            OverallBitRateKbps: 8000,
            FileSizeBytes: 7_200_000_000,
            VideoStreams: [],
            AudioStreams: [],
            SubtitleStreams: [subtitleStream],
            Chapters: [],
            Attachments: attachments
        );

    private static ExecutionPlan MakePlan(SubtitleOutputPlan subtitlePlan) =>
        new(
            Groups:
            [
                new(
                    GroupId: "group_0",
                    Nodes:
                    [
                        new("decode_0", OperationType.Decode, [], new()),
                        new("encode_0", OperationType.Encode, ["decode_0"], new()),
                    ],
                    DeviceId: null,
                    GpuSlotsRequired: 0,
                    CpuThreadsRequired: 4,
                    RequiresGpu: false,
                    Priority: 1
                ),
            ],
            EstimatedTotalDuration: TimeSpan.FromMinutes(90),
            OutputPlan: new(
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
                SubtitleOutputs: [subtitlePlan],
                Thumbnails: null
            )
        );
}
