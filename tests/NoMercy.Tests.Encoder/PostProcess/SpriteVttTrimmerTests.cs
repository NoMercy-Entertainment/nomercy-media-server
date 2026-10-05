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

using NoMercy.Encoder.PostProcess;

namespace NoMercy.Tests.Encoder.PostProcess;

/// <summary>
/// The padding tiles that keep the sheet's last row full are real frames to the
/// muxer, so it writes a cue for each one past the end of the film. The TV seek
/// strip draws every cue it is handed, so those have to go before a client sees
/// them. Cue bodies are written exactly as the muxer emits them.
/// </summary>
public class SpriteVttTrimmerTests
{
    private const string Sample = """
        WEBVTT

        00:00:00.000 --> 00:00:10.000
        thumbs_320x180.webp#xywh=0,0,320,180

        00:00:10.000 --> 00:00:20.000
        thumbs_320x180.webp#xywh=320,0,320,180

        00:00:20.000 --> 00:00:30.000
        thumbs_320x180.webp#xywh=640,0,320,180

        00:00:30.000 --> 00:00:40.000
        thumbs_320x180.webp#xywh=960,0,320,180

        """;

    [Fact]
    public void CuesPastTheEndAreDropped()
    {
        string trimmed = SpriteVttTrimmer.Trim(Sample, TimeSpan.FromSeconds(25));

        CueCount(trimmed).Should().Be(3, "the cue starting at 30s is a padding tile");
        trimmed.Should().Contain("00:00:20.000 --> 00:00:30.000");
        trimmed.Should().NotContain("00:00:30.000 -->");
    }

    [Fact]
    public void ADroppedCueTakesItsPayloadWithIt()
    {
        string trimmed = SpriteVttTrimmer.Trim(Sample, TimeSpan.FromSeconds(25));

        trimmed
            .Should()
            .NotContain(
                "xywh=960,0",
                "a cue body left behind without its timing line corrupts the file"
            );
    }

    [Fact]
    public void TheHeaderSurvives()
    {
        SpriteVttTrimmer.Trim(Sample, TimeSpan.FromSeconds(25)).Should().StartWith("WEBVTT");
    }

    [Fact]
    public void ACueStartingExactlyAtTheEndIsPadding()
    {
        // The first frame past the film starts precisely at its duration.
        string trimmed = SpriteVttTrimmer.Trim(Sample, TimeSpan.FromSeconds(30));

        CueCount(trimmed).Should().Be(3);
    }

    [Fact]
    public void NothingIsDroppedWhenEveryCueIsInsideTheFilm()
    {
        SpriteVttTrimmer
            .Trim(Sample, TimeSpan.FromMinutes(10))
            .Pipe(CueCount)
            .Should()
            .Be(4, "a sheet that happened to fill its grid exactly has no padding");
    }

    [Fact]
    public void SomethingThatIsNotACueListIsLeftAlone()
    {
        const string notVtt = "this is not a cue file";

        SpriteVttTrimmer.Trim(notVtt, TimeSpan.FromSeconds(10)).Should().Be(notVtt);
    }

    private static int CueCount(string vtt) =>
        vtt.Split('\n').Count(line => line.Contains("-->", StringComparison.Ordinal));
}

file static class PipeExtensions
{
    public static TResult Pipe<T, TResult>(this T value, Func<T, TResult> map) => map(value);
}
