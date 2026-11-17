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

using FluentAssertions;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Segments;

namespace NoMercy.Tests.Database.Segments;

[Trait("Category", "Unit")]
public class ContentSegmentPrecedenceTests
{
    private const double Title = ContentSegmentPrecedence.ChapterTitleConfidence;
    private const double Length = ContentSegmentPrecedence.ChapterLengthConfidence;

    [Fact]
    public void The_order_is_manual_then_chapter_titles_then_import_then_detection_then_chapter_lengths()
    {
        List<ContentSegmentRank> highestFirst =
        [
            ContentSegmentPrecedence.RankOf(ContentSegmentSource.Manual, 1.0),
            ContentSegmentPrecedence.RankOf(ContentSegmentSource.Chapters, Title),
            ContentSegmentPrecedence.RankOf(ContentSegmentSource.Import, 1.0),
            ContentSegmentPrecedence.RankOf(ContentSegmentSource.Detector, 1.0),
            ContentSegmentPrecedence.RankOf(ContentSegmentSource.Chapters, Length),
        ];

        highestFirst.Should().BeInDescendingOrder();
        highestFirst.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(ContentSegmentSource.Detector)]
    [InlineData(ContentSegmentSource.Auto)]
    [InlineData(ContentSegmentSource.Analysis)]
    public void The_chromaprint_detector_and_the_analysis_that_replaces_it_share_one_rank(
        string source
    )
    {
        ContentSegmentPrecedence.RankOf(source, 0.4).Should().Be(ContentSegmentRank.Detection);
    }

    [Fact]
    public void An_unknown_source_ranks_as_import_so_no_automatic_writer_deletes_it()
    {
        ContentSegmentPrecedence.RankOf("community-db", 0.5).Should().Be(ContentSegmentRank.Import);
    }

    [Fact]
    public void A_chapter_title_replaces_a_detector_intro()
    {
        ContentSegment detector = Row(ContentSegmentSource.Detector, ContentSegmentType.Intro, 0.8);
        ContentSegment chapter = Row(
            ContentSegmentSource.Chapters,
            ContentSegmentType.Intro,
            Title
        );

        ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
            [detector],
            [chapter],
            ContentSegmentSource.Chapters
        );

        plan.Remove.Should().ContainSingle().Which.Should().BeSameAs(detector);
        plan.Add.Should().ContainSingle().Which.Should().BeSameAs(chapter);
    }

    [Fact]
    public void A_chapter_length_does_not_replace_a_detector_intro()
    {
        ContentSegment detector = Row(ContentSegmentSource.Detector, ContentSegmentType.Intro, 0.8);
        ContentSegment chapter = Row(
            ContentSegmentSource.Chapters,
            ContentSegmentType.Intro,
            Length
        );

        ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
            [detector],
            [chapter],
            ContentSegmentSource.Chapters
        );

        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_detector_run_replaces_a_chapter_length_but_not_a_chapter_title()
    {
        ContentSegment lengthIntro = Row(
            ContentSegmentSource.Chapters,
            ContentSegmentType.Intro,
            Length
        );
        ContentSegment titleOutro = Row(
            ContentSegmentSource.Chapters,
            ContentSegmentType.Outro,
            Title,
            start: 1280
        );
        ContentSegment detectedIntro = Row(
            ContentSegmentSource.Detector,
            ContentSegmentType.Intro,
            0.9
        );
        ContentSegment detectedOutro = Row(
            ContentSegmentSource.Detector,
            ContentSegmentType.Outro,
            0.9,
            start: 1270
        );

        ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
            [lengthIntro, titleOutro],
            [detectedIntro, detectedOutro],
            ContentSegmentSource.Detector
        );

        plan.Remove.Should().ContainSingle().Which.Should().BeSameAs(lengthIntro);
        plan.Add.Should().ContainSingle().Which.Should().BeSameAs(detectedIntro);
    }

    [Fact]
    public void A_rerun_never_overwrites_a_manual_segment()
    {
        ContentSegment manual = Row(ContentSegmentSource.Manual, ContentSegmentType.Intro, 1.0);
        ContentSegment chapter = Row(
            ContentSegmentSource.Chapters,
            ContentSegmentType.Intro,
            Title
        );

        ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
            [manual],
            [chapter],
            ContentSegmentSource.Chapters
        );

        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Manual_credits_block_a_chapter_outro_because_both_are_the_end_of_the_item()
    {
        ContentSegment manualCredits = Row(
            ContentSegmentSource.Manual,
            ContentSegmentType.Credits,
            1.0,
            start: 1280
        );
        ContentSegment chapterOutro = Row(
            ContentSegmentSource.Chapters,
            ContentSegmentType.Outro,
            Title,
            start: 1282
        );

        ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
            [manualCredits],
            [chapterOutro],
            ContentSegmentSource.Chapters
        );

        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_rerun_with_the_same_result_changes_nothing()
    {
        ContentSegment stored = Row(ContentSegmentSource.Chapters, ContentSegmentType.Intro, Title);
        ContentSegment again = Row(ContentSegmentSource.Chapters, ContentSegmentType.Intro, Title);

        ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
            [stored],
            [again],
            ContentSegmentSource.Chapters
        );

        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_rerun_that_no_longer_finds_a_kind_removes_the_writers_old_row_only()
    {
        ContentSegment oldChapterRecap = Row(
            ContentSegmentSource.Chapters,
            ContentSegmentType.Recap,
            Title
        );
        ContentSegment detectorIntro = Row(
            ContentSegmentSource.Detector,
            ContentSegmentType.Intro,
            0.8
        );

        ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
            [oldChapterRecap, detectorIntro],
            [],
            ContentSegmentSource.Chapters
        );

        plan.Remove.Should().ContainSingle().Which.Should().BeSameAs(oldChapterRecap);
        plan.Add.Should().BeEmpty();
    }

    [Fact]
    public void A_newer_detection_replaces_an_older_one_of_equal_rank()
    {
        ContentSegment auto = Row(ContentSegmentSource.Auto, ContentSegmentType.Outro, 0.6);
        ContentSegment detector = Row(ContentSegmentSource.Detector, ContentSegmentType.Outro, 0.7);

        ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
            [auto],
            [detector],
            ContentSegmentSource.Detector
        );

        plan.Remove.Should().ContainSingle().Which.Should().BeSameAs(auto);
        plan.Add.Should().ContainSingle().Which.Should().BeSameAs(detector);
    }

    private static ContentSegment Row(
        string source,
        ContentSegmentType type,
        double confidence,
        double start = 0,
        double end = 90
    ) =>
        new()
        {
            EpisodeId = 1,
            Source = source,
            SegmentType = type,
            Confidence = confidence,
            StartSeconds = start,
            EndSeconds = start + end,
        };
}
