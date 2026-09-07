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
/// The parser against a real commercial recording, not a generated tone.
/// <para>
/// The synthetic fixture proves the three output routes are read. It cannot
/// prove the parser survives real material: a real track carries a leading
/// silence AND a trailing one, a positive-or-negative true peak, an lra, and
/// thousands of metadata frames — and its loudness block only appears because
/// the pass runs at info loglevel.
/// </para>
/// <para>
/// Source: a 4:12 44.1 kHz stereo MP3 from the music library, run through the
/// production filter graph. The stdout fixture keeps every key line and every
/// fourth centroid frame; the stderr fixture is whole.
/// </para>
/// </summary>
public class RealTrackAnalysisParserTests
{
    private static AudioAnalysisResult ParseRealTrack()
    {
        AudioAnalysisOutputParser parser = new();

        foreach (string line in ReadFixture("real-track-stdout.txt"))
        {
            parser.ConsumeStdOut(line);
        }

        foreach (string line in ReadFixture("real-track-stderr.txt"))
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
    public void RealTrack_ProducesEveryMeasurementTheDetectorsOffer()
    {
        AudioAnalysisResult result = ParseRealTrack();

        result.Bpm.Should().NotBeNull();
        result.KeyName.Should().NotBeNull();
        result.KeyConfidence.Should().NotBeNull();
        result.IntegratedLufs.Should().NotBeNull();
        result.TruePeakDb.Should().NotBeNull();
        result.LoudnessRange.Should().NotBeNull();
        result.SpectralCentroid.Should().NotBeNull();
        result.IntroEndMs.Should().NotBeNull();
        result.OutroStartMs.Should().NotBeNull();
    }

    [Fact]
    public void RealTrack_ReadsKeyAndTempo()
    {
        AudioAnalysisResult result = ParseRealTrack();

        result.KeyName.Should().Be("Em");
        result.KeyConfidence.Should().Be(0.752);
        result.Bpm.Should().Be(112.65);
    }

    [Fact]
    public void RealTrack_MapsTheDetectedKeyToCamelot()
    {
        CamelotKey.FromKeyName(ParseRealTrack().KeyName).Should().Be("9A");
    }

    [Fact]
    public void RealTrack_ReadsLoudnessFromTheJsonBlock()
    {
        AudioAnalysisResult result = ParseRealTrack();

        result.IntegratedLufs.Should().Be(-8.55);
        result.TruePeakDb.Should().Be(-0.26);
        result.LoudnessRange.Should().Be(1.80);
    }

    [Fact]
    public void RealTrack_AveragesTheSpectralCentroid()
    {
        ParseRealTrack().SpectralCentroid.Should().BeApproximately(2362.317, 0.01);
    }

    /// <summary>
    /// The track opens with 1.09 s of silence and closes with a fade into
    /// silence at 250.9 s against a 252.19 s duration — so both cue rules are
    /// exercised by one real file rather than by a constructed case.
    /// </summary>
    [Fact]
    public void RealTrack_FindsBothCuePoints()
    {
        AudioAnalysisResult result = ParseRealTrack();

        result.IntroEndMs.Should().Be(1094);
        result.OutroStartMs.Should().Be(250905);
    }

    /// <summary>
    /// The detector emits neither, so a real track must not invent them. This
    /// is what keeps the tempo columns honest until nomercy-ffmpeg#57 lands.
    /// </summary>
    [Fact]
    public void RealTrack_LeavesTempoConfidenceAndDownbeatUnset()
    {
        AudioAnalysisResult result = ParseRealTrack();

        result.BpmConfidence.Should().BeNull();
        result.BeatOffsetMs.Should().BeNull();
    }

    [Fact]
    public void RealTrack_DerivesBeatIntervalFromTempo()
    {
        ParseRealTrack().BeatIntervalMs.Should().BeApproximately(532.624, 0.01);
    }
}
