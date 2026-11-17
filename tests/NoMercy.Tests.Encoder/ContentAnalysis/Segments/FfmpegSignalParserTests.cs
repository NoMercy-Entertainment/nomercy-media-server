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

using NoMercy.Encoder.ContentAnalysis.Segments;
using NoMercy.Encoder.ContentAnalysis.Segments.Probes;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments;

/// <summary>
/// The FFmpeg filter reports, as the research run logged them for Hellsing E01
/// with the server's FFmpeg build.
/// </summary>
[Trait("Category", "Unit")]
public class FfmpegSignalParserTests
{
    [Fact]
    public void Silences_StartAtEndMinusDuration_AndAnOpenRunClosesAtTheEnd()
    {
        string[] lines =
        [
            "[Parsed_silencedetect_1 @ 000002803138a640] silence_start: 1.442323",
            "[Parsed_silencedetect_1 @ 000002803138a640] silence_end: 3.024875 | silence_duration: 1.582552",
            "[Parsed_silencedetect_1 @ 000002803138a640] silence_start: 1377.201333",
        ];

        List<TimeRange> silences = FfmpegSignalParser.Silences(lines, duration: 1380.21);

        silences.Should().HaveCount(2);
        silences[0].Start.Should().BeApproximately(1.442323, 1e-6);
        silences[0].End.Should().BeApproximately(3.024875, 1e-6);
        silences[1].Should().Be(new TimeRange(1377.201333, 1380.21));
    }

    [Fact]
    public void Black_ReadsOneNamedInstance_RelativeToTheWindow()
    {
        string[] lines =
        [
            "[blackdetect@strict @ 000001c90302eec0] black_start:3.12 black_end:5.56 black_duration:2.44",
            "[blackdetect@loose @ 000001c90220f940] black_start:3.08 black_end:6.2 black_duration:3.12",
        ];

        FfmpegSignalParser
            .Black(lines, offset: 79.573, instance: "strict")
            .Should()
            .ContainSingle()
            .Which.Start.Should()
            .BeApproximately(82.693, 1e-9);
        FfmpegSignalParser
            .Black(lines, offset: 0, instance: "loose")
            .Should()
            .Equal(new TimeRange(3.08, 6.2));
    }

    [Fact]
    public void Black_WithoutInstanceName_ReadsTheSingleDetector()
    {
        string[] lines = ["[blackdetect @ 0x55d] black_start:0.8 black_end:1.6 black_duration:0.8"];

        TimeRange run = FfmpegSignalParser
            .Black(lines, offset: 1355.9)
            .Should()
            .ContainSingle()
            .Subject;
        run.Start.Should().BeApproximately(1356.7, 1e-9);
        run.End.Should().BeApproximately(1357.5, 1e-9);
    }

    [Fact]
    public void UnnamedBlackLines_AreRecognised()
    {
        FfmpegSignalParser
            .HasUnnamedBlack([
                "[blackdetect @ 0x1] black_start:5 black_end:8.04 black_duration:3.04",
            ])
            .Should()
            .BeTrue();
        FfmpegSignalParser
            .HasUnnamedBlack([
                "[blackdetect@loose @ 0x1] black_start:3.08 black_end:6.2 black_duration:3.12",
            ])
            .Should()
            .BeFalse();
    }

    [Fact]
    public void SceneCuts_AreTheScdetTimes()
    {
        string[] lines =
        [
            "[scdet @ 0x1] lavfi.scd.score: 34.120, lavfi.scd.time: 5.2",
            "[scdet @ 0x1] lavfi.scd.score: 11.003, lavfi.scd.time: 10.96",
            "frame=  288 fps=0.0 q=-0.0 size=N/A",
        ];

        FfmpegSignalParser
            .SceneCuts(lines, offset: 1356.0)
            .Should()
            .Equal([1361.2, 1366.96], (a, b) => Math.Abs(a - b) < 1e-9);
    }

    [Fact]
    public void InputDuration_IsReadFromTheInputBanner()
    {
        string[] lines = ["  Duration: 00:23:00.21, start: 1.400000, bitrate: 0 kb/s"];

        FfmpegSignalParser.InputDuration(lines).Should().BeApproximately(1380.21, 1e-9);
        FfmpegSignalParser.InputDuration(["nothing"]).Should().BeNull();
    }

    [Fact]
    public void OcrWords_CountsWordsOfThreeOrMoreLetters_PerFrame()
    {
        const string dump =
            "frame:0    pts:0       pts_time:0\nlavfi.ocr.text=9\\'\nMR\n= '6\n\n"
            + "frame:1    pts:1       pts_time:1\nlavfi.ocr.text=Directed by Yasunori Urata\nMusic Hayato Matsuo\n\n";

        List<OcrSample> samples = FfmpegSignalParser.OcrWords(dump, offset: 960.21);

        samples.Should().Equal(new OcrSample(960.21, 0), new OcrSample(961.21, 6));
    }
}
