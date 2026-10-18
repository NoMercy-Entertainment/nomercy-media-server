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

using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Progress;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Output;

public class Mp4OutputStrategyTests
{
    [Fact]
    public void ConfigureOutput_HasFaststart()
    {
        Mp4OutputStrategy strategy = new(TestStorageFactory.CreateLocal());
        FfmpegCommandBuilder builder = new();
        builder.AddInput(new("/input.mkv"));

        strategy.ConfigureOutput(builder, CreatePlan(), "/output");

        FfmpegCommand cmd = builder.Build("ffmpeg");
        string args = string.Join(" ", cmd.Arguments);
        args.Should().Contain("-movflags +faststart");
    }

    [Fact]
    public void ConfigureOutput_ProducesMp4Output()
    {
        Mp4OutputStrategy strategy = new(TestStorageFactory.CreateLocal());
        FfmpegCommandBuilder builder = new();
        builder.AddInput(new("/input.mkv"));

        strategy.ConfigureOutput(builder, CreatePlan(), "/output");

        FfmpegCommand cmd = builder.Build("ffmpeg");
        cmd.Arguments.Should().Contain(a => a.Contains("output.mp4"));
    }

    [Fact]
    public void ConfigureOutput_PreserveDolbyVision_AddsDvh1Tag()
    {
        // DV passthrough in MP4 requires the dvh1 tag — without it Apple TV /
        // QuickTime drop the DV metadata and play as plain HDR10.
        Mp4OutputStrategy strategy = new(TestStorageFactory.CreateLocal());
        FfmpegCommandBuilder builder = new();
        builder.AddInput(new("/input.mkv"));

        OutputPlan plan = CreatePlan() with { PreserveDolbyVision = true };

        strategy.ConfigureOutput(builder, plan, "/output");

        FfmpegCommand cmd = builder.Build("ffmpeg");
        string args = string.Join(" ", cmd.Arguments);
        args.Should().Contain("-tag:v dvh1");
    }

    [Fact]
    public void ConfigureOutput_NoDolbyVision_OmitsDvh1Tag()
    {
        Mp4OutputStrategy strategy = new(TestStorageFactory.CreateLocal());
        FfmpegCommandBuilder builder = new();
        builder.AddInput(new("/input.mkv"));

        strategy.ConfigureOutput(builder, CreatePlan(), "/output");

        FfmpegCommand cmd = builder.Build("ffmpeg");
        string args = string.Join(" ", cmd.Arguments);
        args.Should().NotContain("-tag:v dvh1");
    }

    [Fact]
    public void ConfigureOutput_AudioCopy_EmitsCopyCodecToken()
    {
        // Audio copy must use literal "copy" as the codec — not the source
        // encoder name. ffmpeg interprets "copy" as stream-copy.
        Mp4OutputStrategy strategy = new(TestStorageFactory.CreateLocal());
        FfmpegCommandBuilder builder = new();
        builder.AddInput(new("/input.mkv"));

        OutputPlan plan = CreatePlan() with
        {
            AudioOutputs = [new("aac", 0, 2, 48000, StreamAction.Copy, "eng", "0:a:0")],
        };

        strategy.ConfigureOutput(builder, plan, "/output");

        FfmpegCommand cmd = builder.Build("ffmpeg");
        string args = string.Join(" ", cmd.Arguments);
        args.Should().Contain("-c:a copy");
    }

    [Fact]
    public void ConfigureOutput_AudioFilter_AppliedWhenTranscoding()
    {
        // When the primary audio is transcoded AND carries a filter, the
        // strategy must emit -af with that filter. Required for downmix
        // (e.g. 5.1 → stereo) and loudness normalization.
        Mp4OutputStrategy strategy = new(TestStorageFactory.CreateLocal());
        FfmpegCommandBuilder builder = new();
        builder.AddInput(new("/input.mkv"));

        OutputPlan plan = CreatePlan() with
        {
            AudioOutputs =
            [
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0")
                {
                    AudioFilter = "loudnorm=I=-16:TP=-1.5",
                },
            ],
        };

        strategy.ConfigureOutput(builder, plan, "/output");

        FfmpegCommand cmd = builder.Build("ffmpeg");
        string args = string.Join(" ", cmd.Arguments);
        args.Should().Contain("loudnorm=I=-16:TP=-1.5");
    }

    [Fact]
    public void ConfigureOutput_AudioFilter_OmittedWhenCopy()
    {
        // Stream-copy can't take filters. Strategy must NOT emit -af.
        Mp4OutputStrategy strategy = new(TestStorageFactory.CreateLocal());
        FfmpegCommandBuilder builder = new();
        builder.AddInput(new("/input.mkv"));

        OutputPlan plan = CreatePlan() with
        {
            AudioOutputs =
            [
                new("aac", 0, 2, 48000, StreamAction.Copy, "eng", "0:a:0")
                {
                    AudioFilter = "loudnorm=I=-16",
                },
            ],
        };

        strategy.ConfigureOutput(builder, plan, "/output");

        FfmpegCommand cmd = builder.Build("ffmpeg");
        string args = string.Join(" ", cmd.Arguments);
        args.Should().NotContain("loudnorm=I=-16");
    }

    [Fact]
    public void ConfigureOutput_AudioWithDropAction_NotMapped()
    {
        // Drop = remove the stream entirely; it must not appear in -map.
        Mp4OutputStrategy strategy = new(TestStorageFactory.CreateLocal());
        FfmpegCommandBuilder builder = new();
        builder.AddInput(new("/input.mkv"));

        OutputPlan plan = CreatePlan() with
        {
            AudioOutputs = [new("aac", 0, 2, 48000, StreamAction.Drop, "eng", "0:a:0")],
        };

        strategy.ConfigureOutput(builder, plan, "/output");

        FfmpegCommand cmd = builder.Build("ffmpeg");
        string args = string.Join(" ", cmd.Arguments);
        args.Should().NotContain("-map 0:a:0");
    }

    [Fact]
    public void GetOutputSubdirectories_ReturnsEmpty()
    {
        // MP4 is a single-file container — no subdirectories needed.
        Mp4OutputStrategy strategy = new(TestStorageFactory.CreateLocal());

        strategy.GetOutputSubdirectories(CreatePlan()).Should().BeEmpty();
    }

    [Fact]
    public void Format_IsMp4()
    {
        new Mp4OutputStrategy(TestStorageFactory.CreateLocal())
            .Format.Should()
            .Be(OutputFormat.Mp4);
    }

    [Fact]
    public async Task FinalizeAsync_ChapterRemux_MapsEveryInputStream()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "output.mp4"), [0]);
            CapturingExecutor executor = new();
            Mp4OutputStrategy strategy = new(TestStorageFactory.CreateLocal(), executor);
            OutputPlan plan = CreatePlan() with
            {
                Chapters = [new(TimeSpan.Zero, TimeSpan.FromMinutes(1), "One")],
            };

            await strategy.FinalizeAsync(dir, plan, "title", CancellationToken.None);

            string[] args = executor.Commands.Single().Arguments.ToArray();
            int map = Array.IndexOf(args, "-map");
            map.Should().BeGreaterThanOrEqualTo(0);
            args[map + 1].Should().Be("0");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class CapturingExecutor : IFfmpegExecutor
    {
        public List<FfmpegCommand> Commands { get; } = [];

        public Task<ExecutionResult> ExecuteAsync(
            FfmpegCommand command,
            TimeSpan inputDuration,
            Action<EncodingProgress>? onProgress = null,
            string? correlationId = null,
            CancellationToken ct = default
        )
        {
            Commands.Add(command);
            return Task.FromResult(new ExecutionResult(true, 0, "", TimeSpan.Zero, null));
        }
    }

    private static OutputPlan CreatePlan() =>
        new(
            Format: OutputFormat.Mp4,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    0,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs: [new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0")],
            SubtitleOutputs: [],
            Thumbnails: null
        );
}
