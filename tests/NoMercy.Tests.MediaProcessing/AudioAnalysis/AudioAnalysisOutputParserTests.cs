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

using NoMercy.MediaProcessing.AudioAnalysis;

namespace NoMercy.Tests.MediaProcessing.AudioAnalysis;

/// <summary>
/// The fixtures are the real output of the shipped fork ffmpeg running the
/// production filter graph over a generated click track. The stdout fixture
/// keeps only the three line kinds the parser consumes; the stderr fixture is
/// whole.
/// <para>
/// Source: a 43.5 s mono 44.1 kHz file with a 100 BPM click, 1.5 s of leading
/// silence and 2.1 s of trailing silence.
/// </para>
/// </summary>
public class AudioAnalysisOutputParserTests
{
    private static AudioAnalysisResult ParseFixture()
    {
        AudioAnalysisOutputParser parser = new();

        foreach (string line in ReadFixture("click-100bpm-stdout.txt"))
        {
            parser.ConsumeStdOut(line);
        }

        foreach (string line in ReadFixture("click-100bpm-stderr.txt"))
        {
            parser.ConsumeStdErr(line);
        }

        return parser.Build();
    }

    private static string[] ReadFixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "AudioAnalysis", "Fixtures", name);
        return File.ReadAllLines(path);
    }

    [Fact]
    public void Parse_ReadsTempoFromBeatdetectStderrLine()
    {
        AudioAnalysisResult result = ParseFixture();

        result.Bpm.Should().Be(99.75);
    }

    [Fact]
    public void Parse_DerivesBeatIntervalFromTempo()
    {
        AudioAnalysisResult result = ParseFixture();

        result.BeatIntervalMs.Should().BeApproximately(601.503, 0.001);
    }

    [Fact]
    public void Parse_LeavesConfidenceAndDownbeatNullWhileTheFilterEmitsNeither()
    {
        AudioAnalysisResult result = ParseFixture();

        result.BpmConfidence.Should().BeNull();
        result.BeatOffsetMs.Should().BeNull();
    }

    [Fact]
    public void Parse_TakesTheFinalKeyFromMetadata()
    {
        AudioAnalysisResult result = ParseFixture();

        result.KeyName.Should().Be("Gm");
        result.KeyConfidence.Should().Be(0.413);
    }

    [Fact]
    public void Parse_ReadsLoudnessFromTheLoudnormJsonBlock()
    {
        AudioAnalysisResult result = ParseFixture();

        result.IntegratedLufs.Should().Be(-20.57);
        result.TruePeakDb.Should().Be(-0.60);
        result.LoudnessRange.Should().Be(2.20);
    }

    [Fact]
    public void Parse_AveragesSpectralCentroidAcrossFrames()
    {
        AudioAnalysisResult result = ParseFixture();

        result.SpectralCentroid.Should().BeApproximately(1406.021, 0.01);
    }

    [Fact]
    public void Parse_TakesIntroEndFromLeadingSilence()
    {
        AudioAnalysisResult result = ParseFixture();

        result.IntroEndMs.Should().Be(1500);
    }

    [Fact]
    public void Parse_TakesOutroStartFromSilenceThatRunsToTheEnd()
    {
        AudioAnalysisResult result = ParseFixture();

        result.OutroStartMs.Should().Be(41376);
    }

    [Fact]
    public void Parse_IgnoresSilenceThatDoesNotStartTheFile()
    {
        AudioAnalysisOutputParser parser = new();

        parser.ConsumeStdErr("  Duration: 00:00:60.00, bitrate: 705 kb/s");
        parser.ConsumeStdErr("[silencedetect] silence_start: 12.5");
        parser.ConsumeStdErr("[silencedetect] silence_end: 14.0 | silence_duration: 1.5");

        AudioAnalysisResult result = parser.Build();

        result.IntroEndMs.Should().BeNull();
    }

    [Fact]
    public void Parse_IgnoresAMidTrackGapAsAnOutro()
    {
        AudioAnalysisOutputParser parser = new();

        parser.ConsumeStdErr("  Duration: 00:00:60.00, bitrate: 705 kb/s");
        parser.ConsumeStdErr("[silencedetect] silence_start: 12.5");
        parser.ConsumeStdErr("[silencedetect] silence_end: 14.0 | silence_duration: 1.5");

        AudioAnalysisResult result = parser.Build();

        result.OutroStartMs.Should().BeNull();
    }

    [Fact]
    public void Parse_TreatsAZeroTempoAsAbsentRatherThanZero()
    {
        AudioAnalysisOutputParser parser = new();

        parser.ConsumeStdErr("lavfi.beatdetect.bpm=0.00 ");

        AudioAnalysisResult result = parser.Build();

        result.Bpm.Should().BeNull();
        result.BeatIntervalMs.Should().BeNull();
    }

    [Fact]
    public void Parse_ReturnsNothingFromAnEmptyRun()
    {
        AudioAnalysisOutputParser parser = new();

        AudioAnalysisResult result = parser.Build();

        result.Bpm.Should().BeNull();
        result.KeyName.Should().BeNull();
        result.IntegratedLufs.Should().BeNull();
        result.SpectralCentroid.Should().BeNull();
        result.IntroEndMs.Should().BeNull();
        result.OutroStartMs.Should().BeNull();
    }
}
