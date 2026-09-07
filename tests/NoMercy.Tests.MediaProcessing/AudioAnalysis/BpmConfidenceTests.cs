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
/// Tempo confidence, derived from running the detector twice over the same
/// audio at two sample rates. The filter emits no confidence of its own
/// (nomercy-ffmpeg#57 A3), and resampling must not change a tempo, so the two
/// answers disagreeing is the reliability signal available today.
/// <para>
/// The fixture is a real 44.1 kHz commercial MP3 through the production graph:
/// 99.40 BPM native, 99.20 resampled.
/// </para>
/// </summary>
public class BpmConfidenceTests
{
    private static AudioAnalysisResult ParseDualDetectorFixture()
    {
        AudioAnalysisOutputParser parser = new();

        foreach (string line in ReadFixture("dual-detector-stdout.txt"))
        {
            parser.ConsumeStdOut(line);
        }

        foreach (string line in ReadFixture("dual-detector-stderr.txt"))
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

    private static AudioAnalysisResult ParseTwoDetectors(double first, double second)
    {
        AudioAnalysisOutputParser parser = new();

        parser.ConsumeStdErr(
            $"[Parsed_beatdetect_0 @ 000001a2b1854000] lavfi.beatdetect.bpm={first:F2} "
        );
        parser.ConsumeStdErr(
            $"[Parsed_beatdetect_2 @ 000001a2b1854100] lavfi.beatdetect.bpm={second:F2} "
        );

        return parser.Build();
    }

    /// <summary>
    /// The tempo reported is the one measured on the audio as delivered. The
    /// perturbed copy exists to judge that answer, never to replace it.
    /// </summary>
    [Fact]
    public void RealTrack_ReportsTheFirstDetectorsTempo()
    {
        ParseDualDetectorFixture().Bpm.Should().Be(99.40);
    }

    [Fact]
    public void RealTrack_ScoresATightlyAgreeingPairHigh()
    {
        ParseDualDetectorFixture().BpmConfidence.Should().BeApproximately(0.9799, 0.0001);
    }

    [Fact]
    public void RealTrack_StillReadsEveryOtherMeasurement()
    {
        AudioAnalysisResult result = ParseDualDetectorFixture();

        result.KeyName.Should().Be("C#");
        result.KeyConfidence.Should().Be(0.918);
        result.IntegratedLufs.Should().Be(-9.53);
        result.TruePeakDb.Should().Be(-1.39);
        result.LoudnessRange.Should().Be(8.20);
        result.SpectralCentroid.Should().BeApproximately(2648.652, 0.01);
    }

    [Fact]
    public void PerfectAgreementScoresOne()
    {
        ParseTwoDetectors(128.0, 128.0).BpmConfidence.Should().Be(1.0);
    }

    /// <summary>
    /// Measured on a real library: material the detector cannot hold moves by
    /// 19% to 63% between rates. Anything at or past the trusted spread is worth
    /// nothing, not a little.
    /// </summary>
    [Theory]
    [InlineData(80.0, 130.53)]
    [InlineData(137.74, 111.28)]
    public void MaterialThatMovesWildlyScoresZero(double first, double second)
    {
        ParseTwoDetectors(first, second).BpmConfidence.Should().Be(0.0);
    }

    [Fact]
    public void AFivePercentSpreadScoresHalf()
    {
        ParseTwoDetectors(100.0, 105.0).BpmConfidence.Should().BeApproximately(0.5, 0.0001);
    }

    /// <summary>
    /// One detector is "not measured", which a caller must be able to tell from
    /// "measured and unreliable".
    /// </summary>
    [Fact]
    public void ASingleDetectorLeavesConfidenceNull()
    {
        AudioAnalysisOutputParser parser = new();

        parser.ConsumeStdErr(
            "[Parsed_beatdetect_0 @ 000001a2b1854000] lavfi.beatdetect.bpm=128.00 "
        );

        AudioAnalysisResult result = parser.Build();

        result.Bpm.Should().Be(128.0);
        result.BpmConfidence.Should().BeNull();
    }

    /// <summary>
    /// The filter also writes each value through a bare fprintf carrying no
    /// instance tag. Counting those as detectors would score every single-detector
    /// run as a perfectly agreeing pair.
    /// </summary>
    [Fact]
    public void TheUntaggedDuplicateLineIsNotASecondDetector()
    {
        AudioAnalysisOutputParser parser = new();

        parser.ConsumeStdErr(
            "[Parsed_beatdetect_0 @ 000001a2b1854000] lavfi.beatdetect.bpm=128.00 "
        );
        parser.ConsumeStdErr("lavfi.beatdetect.bpm=128.00 ");

        parser.Build().BpmConfidence.Should().BeNull();
    }

    [Fact]
    public void ADetectorThatFoundNothingIsNotCountedAsAgreement()
    {
        AudioAnalysisOutputParser parser = new();

        parser.ConsumeStdErr(
            "[Parsed_beatdetect_0 @ 000001a2b1854000] lavfi.beatdetect.bpm=128.00 "
        );
        parser.ConsumeStdErr("[Parsed_beatdetect_2 @ 000001a2b1854100] lavfi.beatdetect.bpm=0.00 ");

        AudioAnalysisResult result = parser.Build();

        result.Bpm.Should().Be(128.0);
        result.BpmConfidence.Should().BeNull();
    }
}
