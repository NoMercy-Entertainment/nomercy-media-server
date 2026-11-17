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

using NoMercy.Data.Services.SegmentAnalysis;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.ContentAnalysis.Segments;

namespace NoMercy.Tests.MediaProcessing.Segments;

[Trait("Category", "Unit")]
public class AnalysisSegmentRowsTests
{
    [Fact]
    public void Answer_BecomesAnIntroAndACreditsRow_FromAnalysis()
    {
        List<ContentSegment> rows = AnalysisSegmentRows.For(7, null, SegmentAnswers.Episode());

        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r =>
        {
            r.Source.Should().Be("analysis");
            r.EpisodeId.Should().Be(7);
            r.MovieId.Should().BeNull();
        });
        rows[0].SegmentType.Should().Be(ContentSegmentType.Intro);
        rows[0].StartSeconds.Should().Be(5.56);
        rows[0].EndSeconds.Should().Be(82.72);
        rows[0].Confidence.Should().Be(AnalysisSegmentRows.PictureConfirmedIntro);
        rows[1].SegmentType.Should().Be(ContentSegmentType.Credits);
        rows[1].StartSeconds.Should().Be(1281.64, "the credits row starts at credits_start");
        rows[1].EndSeconds.Should().Be(1361.08);
        rows[1].Confidence.Should().Be(AnalysisSegmentRows.CreditsAndTheme);
    }

    [Fact]
    public void ContentAfterTheCredits_GetsNoRow_SoNoClientSkipsIt()
    {
        List<ContentSegment> rows = AnalysisSegmentRows.For(7, null, SegmentAnswers.Episode());

        rows.Should().NotContain(r => r.EndSeconds > 1361.08, "the preview after the ED is content");
    }

    [Fact]
    public void IntroOnStrongAudioAlone_HasALowerConfidence()
    {
        List<ContentSegment> rows = AnalysisSegmentRows.For(
            7,
            null,
            SegmentAnswers.Episode(intro: SegmentAnswers.Intro(pictureConfirmed: false))
        );

        rows[0].Confidence.Should().Be(AnalysisSegmentRows.StrongAudioIntro);
    }

    [Theory]
    [InlineData(OutroSources.Credits, true, false, AnalysisSegmentRows.PictureCredits)]
    [InlineData(OutroSources.Audio, false, false, AnalysisSegmentRows.ThemeOnlyCredits)]
    [InlineData(OutroSources.Credits | OutroSources.Audio, true, true, AnalysisSegmentRows.CreditsAndTheme)]
    [InlineData(OutroSources.Credits, false, true, AnalysisSegmentRows.ButtonOnlyCredits)]
    public void CreditsConfidence_FollowsTheEvidence(
        OutroSources sources,
        bool dark,
        bool text,
        double confidence
    )
    {
        AnalysisSegmentRows.CreditsConfidence(SegmentAnswers.Credits(sources, dark, text)).Should().Be(confidence);
    }

    [Fact]
    public void OcrOnlyCredits_AreBelowTheButtonOnlyLine()
    {
        // Krapopolis: names over the last scene. A button, never an auto-skip.
        AnalysisSegmentRows.ButtonOnlyCredits.Should().BeLessThan(0.5);
        AnalysisSegmentRows.PictureCredits.Should().BeGreaterThanOrEqualTo(0.5);
        AnalysisSegmentRows.ThemeOnlyCredits.Should().BeGreaterThanOrEqualTo(0.5);
    }

    [Fact]
    public void Movie_RowsCarryTheMovieId()
    {
        List<ContentSegment> rows = AnalysisSegmentRows.For(null, 129, SegmentAnswers.Episode(withIntro: false));

        rows.Should().ContainSingle().Which.MovieId.Should().Be(129);
    }
}
