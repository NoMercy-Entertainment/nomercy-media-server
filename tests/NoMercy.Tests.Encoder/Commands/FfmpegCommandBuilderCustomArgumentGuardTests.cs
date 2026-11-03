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
using NoMercy.Encoder.LiveTranscode;

namespace NoMercy.Tests.Encoder.Commands;

/// <summary>
///     A profile stored before the unsafe-flag rule existed can still carry a dangerous custom
///     argument. The command builders drop that one argument and keep encoding.
/// </summary>
public class FfmpegCommandBuilderCustomArgumentGuardTests
{
    private static FfmpegCommand BuildWith(
        Dictionary<string, string>? globalFlags,
        Dictionary<string, string>? outputFlags
    ) =>
        new FfmpegCommandBuilder()
            .WithGlobalExtraFlags(globalFlags)
            .AddInput(new("/input/video.mkv"))
            .AddOutput(new(FilePath: "/output/video.mp4", ExtraFlags: outputFlags))
            .Build("ffmpeg");

    [Theory]
    [InlineData("-i", "/input/other.mkv")]
    [InlineData("-progress", "/output/progress.txt")]
    [InlineData("-vstats_file", "/output/vstats.txt")]
    [InlineData("-report", "1")]
    [InlineData("-dump_attachment:t", "/output/font.ttf")]
    [InlineData("-attach", "/input/font.ttf")]
    [InlineData("-filter_script:v", "/input/script.txt")]
    [InlineData("-filter_complex_script", "/input/script.txt")]
    [InlineData("-protocol_whitelist", "file,http")]
    [InlineData("-/filter:v", "/input/script.txt")]
    public void GlobalFlag_DeniedKey_IsDropped(string key, string value)
    {
        FfmpegCommand cmd = BuildWith(new() { [key] = value, ["-threads"] = "2" }, null);
        FfmpegCommand baseline = BuildWith(new() { ["-threads"] = "2" }, null);

        cmd.Arguments.Count(a => a == key).Should().Be(baseline.Arguments.Count(a => a == key));
        cmd.Arguments.Should().NotContain(value);
        cmd.Arguments.Should().ContainInConsecutiveOrder("-threads", "2");
    }

    [Theory]
    [InlineData("-i", "/input/other.mkv")]
    [InlineData("-vstats_file", "/output/vstats.txt")]
    [InlineData("-attach", "/input/font.ttf")]
    [InlineData("-filter_script:v", "/input/script.txt")]
    public void OutputFlag_DeniedKey_IsDropped(string key, string value)
    {
        FfmpegCommand cmd = BuildWith(null, new() { [key] = value, ["-aac_coder"] = "twoloop" });
        FfmpegCommand baseline = BuildWith(null, new() { ["-aac_coder"] = "twoloop" });

        cmd.Arguments.Count(a => a == key).Should().Be(baseline.Arguments.Count(a => a == key));
        cmd.Arguments.Should().NotContain(value);
        cmd.Arguments.Should().ContainInConsecutiveOrder("-aac_coder", "twoloop");
        cmd.Arguments.Should().Contain("/output/video.mp4");
    }

    [Theory]
    [InlineData("scale=1280:720,movie=example.mkv[b];[0:v][b]overlay")]
    [InlineData("amovie=example.mka")]
    [InlineData("subtitles=example.srt")]
    [InlineData("ass=example.ass")]
    [InlineData("drawtext=textfile=example.txt")]
    [InlineData("lut3d=file=example.cube")]
    public void FilterSourceValue_IsDropped(string value)
    {
        FfmpegCommand cmd = BuildWith(
            new() { ["-some_option"] = value },
            new() { ["-other_option"] = value, ["-fps_mode"] = "cfr" }
        );

        cmd.Arguments.Should().NotContain("-some_option");
        cmd.Arguments.Should().NotContain("-other_option");
        cmd.Arguments.Should().NotContain(value);
        cmd.Arguments.Should().ContainInConsecutiveOrder("-fps_mode", "cfr");
    }

    [Fact]
    public void LegitimateFlags_AreUnchanged()
    {
        FfmpegCommand cmd = BuildWith(
            new() { ["-threads"] = "4", ["-max_muxing_queue_size"] = "1024" },
            new()
            {
                ["-x264-params"] = "profile=high:aq-mode=3",
                ["-vf"] = "scale=240:-2",
                ["-passlogfile"] = "/output/pass",
                ["-an"] = "",
            }
        );

        cmd.Arguments.Should().ContainInConsecutiveOrder("-threads", "4");
        cmd.Arguments.Should().ContainInConsecutiveOrder("-max_muxing_queue_size", "1024");
        cmd.Arguments.Should().ContainInConsecutiveOrder("-x264-params", "profile=high:aq-mode=3");
        cmd.Arguments.Should().ContainInConsecutiveOrder("-vf", "scale=240:-2");
        cmd.Arguments.Should().ContainInConsecutiveOrder("-passlogfile", "/output/pass");
        cmd.Arguments.Should().Contain("-an");
    }

    [Fact]
    public void LiveBuilder_DropsUnsafeCustomArguments_AndKeepsTheRest()
    {
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: new(
                Id: "720p",
                Label: "720p",
                Width: 1280,
                Height: 720,
                Codec: VideoCodecType.H264,
                BitrateKbps: 4000,
                Encoder: "libx264",
                IsHardwareAccelerated: false,
                ExpectedSpeed: 2.0,
                CanRealtime: true
            ),
            SegmentDurationSeconds: 4,
            CustomArguments: new Dictionary<string, string>
            {
                ["-x264-params"] = "nal-hrd=cbr",
                ["-vstats_file"] = "/tmp/example-vstats.txt",
                ["-some_option"] = "/etc/example",
                ["-other_option"] = "movie=example.mkv",
            }
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        args.Should().ContainInConsecutiveOrder("-x264-params", "nal-hrd=cbr");
        args.Should().NotContain("-vstats_file");
        args.Should().NotContain("/tmp/example-vstats.txt");
        args.Should().NotContain("-some_option");
        args.Should().NotContain("-other_option");
    }
}
