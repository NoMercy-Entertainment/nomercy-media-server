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
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Optimizer;
using NoMercy.Encoder.Pipeline.Resume;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.PostProcess;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Pipeline.Stages;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Pipeline.Resume;

/// <summary>
/// The first build starts at zero and leaves a marker; a restart over the same
/// working folder and the same arguments continues at the segment boundary.
/// </summary>
public class BuildStageEncodeResumeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"resume_build_{Guid.NewGuid():N}"
    );
    private readonly BuildStage _stage;
    private readonly EncodingContext _context = EncodingContext.Create();

    public BuildStageEncodeResumeTests()
    {
        Directory.CreateDirectory(_dir);
        IStorage storage = TestStorageFactory.CreateLocal();
        EncoderOptions options = new()
        {
            FfmpegPathOverride = "ffmpeg",
            FfprobePathOverride = "ffprobe",
        };
        EncodeResumeResolver resolver = new(
            storage,
            new SegmentNumberProber(),
            new(storage),
            NullLogger<EncodeResumeResolver>.Instance
        );
        _stage = new(
            options,
            new FontExtractor(storage),
            new SubtitleExtractor(),
            OutputStrategyFactoryTestHelper.Create(),
            [],
            NullLogger<BuildStage>.Instance,
            storage,
            resumeResolver: resolver
        );
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task FirstBuild_StartsAtZero_AndWritesTheMarker()
    {
        FfmpegCommand[] commands = await Build(crf: 23);

        commands[0].Arguments.Should().NotContain("-start_number");
        commands[0].Arguments.Should().NotContain("-ss");
        commands[0].ResumeMarkerPath.Should().BeNull();
        Directory.GetFiles(_dir, ".resume.*").Should().ContainSingle();
    }

    [Fact]
    public async Task RestartWithSameArguments_SeeksToTheBoundaryAndNumbersTheSegments()
    {
        FfmpegCommand[] first = await Build(crf: 23);
        WriteSegments(first[0], 6);

        FfmpegCommand[] second = await Build(crf: 23);

        string[] args = second[0].Arguments;
        int seek = Array.IndexOf(args, "-ss");
        seek.Should().BeGreaterThanOrEqualTo(0).And.BeLessThan(Array.IndexOf(args, "-i"));
        args[seek + 1].Should().Be("10.010");
        args.Where(a => a == "-start_number").Should().HaveCount(2);
        args.Where(a => a == "-output_ts_offset").Should().HaveCount(2);
        args[Array.IndexOf(args, "-start_number") + 1].Should().Be("5");
        args[Array.IndexOf(args, "-output_ts_offset") + 1].Should().Be("10.010");
        second[0].ResumeMarkerPath.Should().Be(Directory.GetFiles(_dir, ".resume.*").Single());
    }

    [Fact]
    public async Task RestartWithChangedArguments_StartsAtZero()
    {
        FfmpegCommand[] first = await Build(crf: 23);
        WriteSegments(first[0], 6);

        FfmpegCommand[] second = await Build(crf: 18);

        second[0].Arguments.Should().NotContain("-start_number");
        second[0].Arguments.Should().NotContain("-ss");
    }

    private async Task<FfmpegCommand[]> Build(int crf)
    {
        StageResult result = await _stage.ExecuteAsync(
            BuildInputFactory(crf),
            _context,
            CancellationToken.None
        );
        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();
        return ((StageSuccess<FfmpegCommand[]>)result).Value;
    }

    private void WriteSegments(FfmpegCommand command, int count)
    {
        string[] args = command.Arguments;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "-hls_segment_filename")
                continue;

            string pattern = Path.Combine(_dir, args[i + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(pattern)!);
            for (int n = 0; n < count; n++)
                File.WriteAllBytes(pattern.Replace("%05d", n.ToString("D5")), [1]);
        }
    }

    private BuildInput BuildInputFactory(int crf)
    {
        ExecutionPlan plan = new(
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
                        Crf: crf,
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
                Thumbnails: null
            )
        );

        return new(Plan: plan, InputPath: "/dev/null", OutputDirectory: _dir, MediaTitle: "test");
    }

    /// <summary>Segment n starts at 1.4 s plus 2.002 s per segment.</summary>
    private sealed class SegmentNumberProber : ISegmentProber
    {
        public Task<double?> FirstPacketPtsAsync(
            string segmentPath,
            bool video,
            CancellationToken ct
        )
        {
            string name = Path.GetFileNameWithoutExtension(segmentPath);
            int number = int.Parse(name[^5..]);
            return Task.FromResult<double?>(1.4 + 2.002 * number);
        }
    }
}
