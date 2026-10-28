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

using System.Text.RegularExpressions;
using NoMercy.Encoder.Commands;

namespace NoMercy.Tests.Encoder.Scenarios;

/// <summary>
/// Invariant scenario tests for FfmpegCommandBuilder. These tests verify structural
/// guarantees across diverse ffmpeg command shapes — they do NOT test actual encoding
/// output, but rather the mechanical correctness of argv construction.
///
/// Each scenario models a realistic encoding plan (single-rung, ladder, copy+transcode,
/// etc.) and asserts invariants that MUST hold or the command is malformed.
/// If a test fails, the builder is emitting broken ffmpeg syntax.
/// </summary>
public partial class CommandBuilderInvariantScenarioTests
{
    [GeneratedRegex(@"\[(\w+)\]", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FilterPad();

    private const string FfmpegPath = "ffmpeg";
    private const string InputPath = "/media/source.mkv";
    private const string OutputPath = "/output/video.mp4";

    [Fact]
    public void SingleRungCopy_NoFilterComplex_VerifiesNoFilterGraphPads()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(OutputPath, VideoCodec: "copy", AudioCodec: "copy", MapStreams: ["0:v", "0:a"])
            )
            .Build(FfmpegPath);

        string argString = string.Join(" ", cmd.Arguments);
        argString
            .Should()
            .NotContain("-filter_complex", "copy codec should not use filter_complex");

        int mapIdx = 0;
        while ((mapIdx = Array.IndexOf(cmd.Arguments, "-map", mapIdx)) >= 0)
        {
            if ((mapIdx + 1) < cmd.Arguments.Length)
            {
                string mapValue = cmd.Arguments[mapIdx + 1];
                mapValue
                    .Should()
                    .NotStartWith("[", "copy output should not map from filter graph labels");
            }
            mapIdx++;
        }
    }

    [Fact]
    public void SingleRungCopy_CopyCodecHasNoEncoderOnlyFlags()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(new(OutputPath, VideoCodec: "copy", MapStreams: ["0:v"]))
            .Build(FfmpegPath);

        int[] copyArgIndices =
        [
            .. cmd
                .Arguments.Select((arg, idx) => (arg, idx))
                .Where(x => x.arg == "copy")
                .Select(x => x.idx),
        ];

        copyArgIndices.Should().NotBeEmpty();

        int outputStartIdx = Array.IndexOf(cmd.Arguments, OutputPath);
        string[] argRange = [.. cmd.Arguments.Take(outputStartIdx)];

        argRange
            .Should()
            .NotContain("-crf", "copy codec should not have -crf")
            .And.NotContain("-preset", "copy codec should not have -preset");
    }

    [Fact]
    public void SingleRungTranscode_VideoCodecFollowedByCodecValue()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(
                    OutputPath,
                    VideoCodec: "libx264",
                    Crf: 23,
                    Preset: "medium",
                    MapStreams: ["0:v"]
                )
            )
            .Build(FfmpegPath);

        int codecIdx = Array.IndexOf(cmd.Arguments, "-c:v");
        codecIdx.Should().BeGreaterThan(-1);
        (codecIdx + 1).Should().BeLessThan(cmd.Arguments.Length);
        cmd.Arguments[codecIdx + 1].Should().Be("libx264");

        cmd.Arguments.Should().Contain("-crf");
        int crfIdx = Array.IndexOf(cmd.Arguments, "-crf");
        (crfIdx + 1).Should().BeLessThan(cmd.Arguments.Length);
        cmd.Arguments[crfIdx + 1].Should().Be("23");
    }

    [Fact]
    public void MultiOutputWithFilterComplex_AllPadsReferencedInFilterGraphHaveMapStreams()
    {
        string filterGraph = "[0:v]split=2[v0][v1];[v0]copy[v0_out];[v1]scale=1280:-2[v1_out]";

        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .WithFilterComplex(filterGraph)
            .AddOutput(
                new("/output/v1.mp4", VideoCodec: "libx264", MapStreams: ["[v0_out]"], Crf: 23)
            )
            .AddOutput(
                new("/output/v2.mp4", VideoCodec: "libx264", MapStreams: ["[v1_out]"], Crf: 23)
            )
            .Build(FfmpegPath);

        MatchCollection padMatches = FilterPad().Matches(filterGraph);
        string[] outputPads =
        [
            .. padMatches.Cast<Match>().Select(m => m.Groups[1].Value).Distinct(),
        ];

        int mapIdx = 0;
        foreach (string pad in outputPads)
        {
            mapIdx = Array.IndexOf(cmd.Arguments, "-map", mapIdx);
            if (mapIdx > -1 && (mapIdx + 1) < cmd.Arguments.Length)
            {
                string mapValue = cmd.Arguments[mapIdx + 1];
                if (mapValue.StartsWith("[") && mapValue.EndsWith("]"))
                {
                    outputPads
                        .Should()
                        .Contain(
                            mapValue.Trim('[', ']'),
                            "mapped label must be produced by -filter_complex"
                        );
                }
            }
        }
    }

    [Fact]
    public void AllFlagsWithValuesHaveFollowingValue_NoDanglingFlags()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(
                new(
                    Overwrite: true,
                    HideBanner: false,
                    ProgressPipe: true,
                    Threads: 4,
                    AnalyzeDurationUs: 5_000_000
                )
            )
            .AddInput(new(InputPath, SeekTo: TimeSpan.FromSeconds(1.5)))
            .AddOutput(
                new(
                    OutputPath,
                    VideoCodec: "libx264",
                    Crf: 23,
                    Preset: "medium",
                    Profile: "high",
                    MapStreams: ["0:v"]
                )
            )
            .Build(FfmpegPath);

        string[] flagsWithRequiredValues =
        [
            "-threads",
            "-analyzeduration",
            "-ss",
            "-i",
            "-c:v",
            "-crf",
            "-preset",
            "-profile:v",
            "-map",
            "-progress",
        ];

        for (int i = 0; i < cmd.Arguments.Length; i++)
        {
            string arg = cmd.Arguments[i];
            if (flagsWithRequiredValues.Contains(arg))
            {
                (i + 1)
                    .Should()
                    .BeLessThan(
                        cmd.Arguments.Length,
                        $"Flag {arg} at position {i} lacks a following value"
                    );
                string nextArg = cmd.Arguments[i + 1];
                nextArg
                    .Should()
                    .NotStartWith(
                        "-",
                        $"Value for {arg} should not start with '-'; got '{nextArg}'"
                    );
            }
        }
    }

    [Fact]
    public void NoGlobalFlagDuplicates_SingleDashY_SingleDashProgress()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: true, HideBanner: true, ProgressPipe: true))
            .AddInput(new(InputPath))
            .AddOutput(new(OutputPath, VideoCodec: "copy", MapStreams: ["0:v"]))
            .Build(FfmpegPath);

        int yCount = cmd.Arguments.Count(arg => arg == "-y");
        yCount.Should().Be(1, "-y should appear exactly once");

        int progressCount = cmd.Arguments.Count(arg => arg == "-progress");
        progressCount.Should().Be(1, "-progress should appear exactly once");

        int hideBannerCount = cmd.Arguments.Count(arg => arg == "-hide_banner");
        hideBannerCount.Should().Be(1, "-hide_banner should appear exactly once");
    }

    [Fact]
    public void AudioOnlyOutput_NoVideoCodecNoHevcTags()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(OutputPath, AudioCodec: "aac", AudioBitrateKbps: 192, MapStreams: ["0:a"])
            )
            .Build(FfmpegPath);

        cmd.Arguments.Should()
            .NotContain("libx264", "audio-only output should not reference video codec");
        cmd.Arguments.Should().NotContain("-tag:v", "audio-only output should not use video tags");

        string argString = string.Join(" ", cmd.Arguments);
        argString.Should().NotContain("hvc1", "audio-only should not reference hvc1");
    }

    [Fact]
    public void HevcOutputToMp4Container_IncludesHvc1Tag()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(
                    "/output/video.mp4",
                    VideoCodec: "libx265",
                    Crf: 23,
                    MapStreams: ["0:v"],
                    ExtraFlags: new() { { "-tag:v", "hvc1" } }
                )
            )
            .Build(FfmpegPath);

        int tagVIdx = Array.IndexOf(cmd.Arguments, "-tag:v");
        tagVIdx.Should().BeGreaterThan(-1, "-tag:v should be present for HEVC in MP4");
        cmd.Arguments[tagVIdx + 1].Should().Be("hvc1");
    }

    [Fact]
    public void DolbyVisionOutput_IncludesDvh1Tag()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(
                    OutputPath,
                    VideoCodec: "libx265",
                    Crf: 23,
                    MapStreams: ["0:v"],
                    ExtraFlags: new() { { "-tag:v", "dvh1" } }
                )
            )
            .Build(FfmpegPath);

        int dvh1Idx = Array.IndexOf(cmd.Arguments, "-tag:v");
        dvh1Idx.Should().BeGreaterThan(-1);
        cmd.Arguments[dvh1Idx + 1].Should().Be("dvh1");
    }

    [Fact]
    public void CustomArgumentsProfile_GlobalExtraFlagsAppearBeforeInputs()
    {
        Dictionary<string, string> customGlobalFlags = new() { { "-hwaccel", "cuda" } };

        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .WithGlobalExtraFlags(customGlobalFlags)
            .AddInput(new(InputPath))
            .AddOutput(new(OutputPath, VideoCodec: "copy", MapStreams: ["0:v"]))
            .Build(FfmpegPath);

        int hwaccelIdx = Array.IndexOf(cmd.Arguments, "-hwaccel");
        int inputIdx = Array.IndexOf(cmd.Arguments, "-i");

        hwaccelIdx.Should().BeGreaterThan(-1, "custom -hwaccel should be present");
        hwaccelIdx.Should().BeLessThan(inputIdx, "-hwaccel must appear before -i");
    }

    [Fact]
    public void CustomArgumentsProfile_OutputExtraFlagsAppearInOutput()
    {
        Dictionary<string, string> outputCustomFlags = new() { { "-color_primaries", "bt709" } };

        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(
                    OutputPath,
                    VideoCodec: "libx264",
                    Crf: 23,
                    MapStreams: ["0:v"],
                    ExtraFlags: outputCustomFlags
                )
            )
            .Build(FfmpegPath);

        int colorPrimariesIdx = Array.IndexOf(cmd.Arguments, "-color_primaries");
        colorPrimariesIdx
            .Should()
            .BeGreaterThan(-1, "output ExtraFlags should be emitted in the command");
        cmd.Arguments[colorPrimariesIdx + 1].Should().Be("bt709");

        int outputPathIdx = Array.IndexOf(cmd.Arguments, OutputPath);
        colorPrimariesIdx
            .Should()
            .BeLessThan(outputPathIdx, "extra flags must appear before output path");
    }

    [Fact]
    public void BooleanExtraFlagWithEmptyValue_OmitsEmptyToken()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(
                    OutputPath,
                    VideoCodec: "copy",
                    MapStreams: ["0:v"],
                    ExtraFlags: new() { { "-an", "" } }
                )
            )
            .Build(FfmpegPath);

        int anIdx = Array.IndexOf(cmd.Arguments, "-an");
        anIdx.Should().BeGreaterThan(-1, "boolean flag -an should be present");

        if (anIdx + 1 < cmd.Arguments.Length)
        {
            string nextArg = cmd.Arguments[anIdx + 1];
            nextArg.Should().NotBe("", "empty string value should not produce an empty token");
        }
    }

    [Fact]
    public void MultipleInputs_AllInputsHaveDashI()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new("/media/source1.mkv"))
            .AddInput(new("/media/source2.mkv"))
            .AddOutput(new(OutputPath, VideoCodec: "copy", MapStreams: ["0:v", "1:a"]))
            .Build(FfmpegPath);

        int inputCount = cmd.Arguments.Count(arg => arg == "-i");
        inputCount.Should().Be(2, "two inputs should produce two -i flags");
    }

    [Fact]
    public void OutputMetadataStripping_IncludesMapMetadata()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(OutputPath, VideoCodec: "copy", MapStreams: ["0:v"], StripSourceMetadata: true)
            )
            .Build(FfmpegPath);

        int mapMetadataIdx = Array.IndexOf(cmd.Arguments, "-map_metadata");
        mapMetadataIdx.Should().BeGreaterThan(-1, "-map_metadata -1 should strip source metadata");
        cmd.Arguments[mapMetadataIdx + 1].Should().Be("-1");
    }

    [Fact]
    public void OutputStreamMetadata_AppearsBeforeOutputPath()
    {
        OutputStreamTag[] tags = [new("v:0", "language", "eng"), new("a:0", "title", "English")];

        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(
                    OutputPath,
                    VideoCodec: "copy",
                    MapStreams: ["0:v", "0:a"],
                    StreamMetadata: tags
                )
            )
            .Build(FfmpegPath);

        string argString = string.Join(" ", cmd.Arguments);
        argString.Should().Contain("-metadata:v:0", "stream metadata should include language tag");

        int metadataIdx = Array.IndexOf(cmd.Arguments, "-metadata:v:0");
        int outputPathIdx = Array.IndexOf(cmd.Arguments, OutputPath);

        metadataIdx.Should().BeLessThan(outputPathIdx, "metadata must appear before output path");
    }

    [Fact]
    public void TwPassEncoding_Pass1VideoOnlyHasStatsFile()
    {
        Dictionary<string, string> pass1Flags = new()
        {
            { "-pass", "1" },
            { "-passlogfile", "/tmp/stats" },
        };

        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new("/dev/null", VideoCodec: "libx264", MapStreams: ["0:v"], ExtraFlags: pass1Flags)
            )
            .Build(FfmpegPath);

        int pass1Idx = Array.IndexOf(cmd.Arguments, "-pass");
        pass1Idx.Should().BeGreaterThan(-1, "pass 1 should include -pass flag");
        cmd.Arguments[pass1Idx + 1].Should().Be("1");

        int passlogIdx = Array.IndexOf(cmd.Arguments, "-passlogfile");
        passlogIdx.Should().BeGreaterThan(-1, "-passlogfile should be present");
    }

    [Fact]
    public void FilterComplexAppears_BetweenInputsAndOutputs()
    {
        string filterGraph = "[0:v]scale=1280:-2[scaled]";

        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .WithFilterComplex(filterGraph)
            .AddOutput(new(OutputPath, VideoCodec: "libx264", Crf: 23, MapStreams: ["[scaled]"]))
            .Build(FfmpegPath);

        int inputIdx = Array.IndexOf(cmd.Arguments, "-i");
        int filterComplexIdx = Array.IndexOf(cmd.Arguments, "-filter_complex");
        int mapIdx = Array.IndexOf(cmd.Arguments, "-map");

        inputIdx.Should().BeLessThan(filterComplexIdx, "-i must come before -filter_complex");
        filterComplexIdx.Should().BeLessThan(mapIdx, "-filter_complex must come before -map");
    }

    [Fact]
    public void CultureInvariantSeekAndDuration_NoCommaSeparators()
    {
        System.Globalization.CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new("de-DE");

            FfmpegCommand cmd = new FfmpegCommandBuilder()
                .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
                .AddInput(
                    new(
                        InputPath,
                        SeekTo: TimeSpan.FromMilliseconds(12_345),
                        Duration: TimeSpan.FromMilliseconds(7_500)
                    )
                )
                .AddOutput(new(OutputPath, VideoCodec: "copy", MapStreams: ["0:v"]))
                .Build(FfmpegPath);

            string argString = string.Join(" ", cmd.Arguments);
            argString.Should().Contain("12.345", "seek should use dot separator not comma");
            argString.Should().Contain("7.500", "duration should use dot separator not comma");
            argString.Should().NotContain("12,345", "de-DE comma should not appear");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void OutputPathIsAlwaysLastInItsBlock_NoArgsAfterIt()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(new(OutputPath, VideoCodec: "libx264", Crf: 23, MapStreams: ["0:v"]))
            .Build(FfmpegPath);

        int outputPathIdx = Array.IndexOf(cmd.Arguments, OutputPath);
        outputPathIdx
            .Should()
            .Be(cmd.Arguments.Length - 1, "output path should be the last argument in the command");
    }

    [Fact]
    public void MultipleOutputs_EachHasOwnPath_AtEndOfItsBlock()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .WithFilterComplex("[0:v]split=2[v0][v1]")
            .AddOutput(
                new("/output/out1.mp4", VideoCodec: "libx264", Crf: 23, MapStreams: ["[v0]"])
            )
            .AddOutput(
                new("/output/out2.mp4", VideoCodec: "libx265", Crf: 24, MapStreams: ["[v1]"])
            )
            .Build(FfmpegPath);

        int path1Idx = Array.IndexOf(cmd.Arguments, "/output/out1.mp4");
        int path2Idx = Array.IndexOf(cmd.Arguments, "/output/out2.mp4");

        path1Idx.Should().BeGreaterThan(-1);
        path2Idx.Should().BeGreaterThan(-1);
        path1Idx.Should().BeLessThan(path2Idx, "paths must appear in declaration order");
        path2Idx.Should().Be(cmd.Arguments.Length - 1, "last output path must be at the end");
    }

    [Fact]
    public void AudioBitrate_ProducesDashBColonA_WithKSuffix()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(OutputPath, AudioCodec: "aac", AudioBitrateKbps: 192, MapStreams: ["0:a"])
            )
            .Build(FfmpegPath);

        int bitRateIdx = Array.IndexOf(cmd.Arguments, "-b:a");
        bitRateIdx.Should().BeGreaterThan(-1);
        cmd.Arguments[bitRateIdx + 1]
            .Should()
            .Be("192k", "audio bitrate should include 'k' suffix");
    }

    [Fact]
    public void VideoBitrate_ProducesDashBColonV_WithKSuffix()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(OutputPath, VideoCodec: "libx264", VideoBitrateKbps: 5000, MapStreams: ["0:v"])
            )
            .Build(FfmpegPath);

        int bitRateIdx = Array.IndexOf(cmd.Arguments, "-b:v");
        bitRateIdx.Should().BeGreaterThan(-1);
        cmd.Arguments[bitRateIdx + 1]
            .Should()
            .Be("5000k", "video bitrate should include 'k' suffix");
    }

    [Fact]
    public void ProfileAndLevel_AppearsForVideoOutput()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(
                    OutputPath,
                    VideoCodec: "libx264",
                    Profile: "high",
                    Level: "4.1",
                    MapStreams: ["0:v"]
                )
            )
            .Build(FfmpegPath);

        int profileIdx = Array.IndexOf(cmd.Arguments, "-profile:v");
        profileIdx.Should().BeGreaterThan(-1);
        cmd.Arguments[profileIdx + 1].Should().Be("high");

        int levelIdx = Array.IndexOf(cmd.Arguments, "-level");
        levelIdx.Should().BeGreaterThan(-1);
        cmd.Arguments[levelIdx + 1].Should().Be("4.1");
    }

    [Fact]
    public void PixelFormat_AppearsForVideoOutput()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(OutputPath, VideoCodec: "libx264", PixelFormat: "yuv420p", MapStreams: ["0:v"])
            )
            .Build(FfmpegPath);

        int pixFmtIdx = Array.IndexOf(cmd.Arguments, "-pix_fmt");
        pixFmtIdx.Should().BeGreaterThan(-1);
        cmd.Arguments[pixFmtIdx + 1].Should().Be("yuv420p");
    }

    [Fact]
    public void KeyframeInterval_AppearsForVideoOutput()
    {
        FfmpegCommand cmd = new FfmpegCommandBuilder()
            .WithGlobalOptions(new(Overwrite: false, HideBanner: false, ProgressPipe: false))
            .AddInput(new(InputPath))
            .AddOutput(
                new(OutputPath, VideoCodec: "libx264", KeyframeInterval: 120, MapStreams: ["0:v"])
            )
            .Build(FfmpegPath);

        int gIdx = Array.IndexOf(cmd.Arguments, "-g");
        gIdx.Should().BeGreaterThan(-1);
        cmd.Arguments[gIdx + 1].Should().Be("120");
    }
}
