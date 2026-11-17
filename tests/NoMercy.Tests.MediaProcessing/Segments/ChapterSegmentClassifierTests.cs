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

using NoMercy.Database.Models.Media;
using NoMercy.MediaProcessing.Segments;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>
/// One rule per test. Chapter lists are written as (title, length) pairs that
/// follow each other from 0 s.
/// </summary>
[Trait("Category", "Unit")]
public class ChapterSegmentClassifierTests
{
    private readonly ChapterSegmentClassifier _classifier = new();

    [Theory]
    [InlineData("OP")]
    [InlineData("NCOP")]
    [InlineData("opening")]
    [InlineData("Opening Credits")]
    [InlineData("Opening Theme")]
    [InlineData("Opening Song")]
    public void An_opening_title_is_the_intro_at_any_length(string title)
    {
        IReadOnlyList<ClassifiedChapterSegment> found = Classify(
            false,
            ("Prologue", 60),
            (title, 30),
            ("Part A", 600),
            ("Part B", 600)
        );

        found
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(new ClassifiedChapterSegment(ContentSegmentType.Intro, 60, 90, ChapterSegmentMethod.Title));
    }

    [Theory]
    [InlineData(85)]
    [InlineData(90)]
    [InlineData(95)]
    public void Intro_is_the_opening_song_only_at_85_to_95_seconds(double length)
    {
        Classify(false, ("Intro", length), ("Part A", 600), ("Part B", 600))
            .Should()
            .ContainSingle(s => s.Type == ContentSegmentType.Intro);
    }

    [Theory]
    [InlineData(28)]
    [InlineData(84)]
    [InlineData(96)]
    [InlineData(227)]
    public void Intro_of_any_other_length_is_a_cold_open_and_plays(double length)
    {
        Classify(false, ("Intro", length), ("Part A", 600), ("Part B", 600))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Ending_and_preview_titles_become_one_outro()
    {
        Classify(false, ("OP", 90), ("Part A", 600), ("Part B", 600), ("ED", 90), ("Preview", 15))
            .Should()
            .Contain(new ClassifiedChapterSegment(ContentSegmentType.Outro, 1290, 1395, ChapterSegmentMethod.Title));
    }

    [Fact]
    public void An_untitled_epilogue_after_the_credits_plays()
    {
        Classify(false, ("Part 1", 600), ("Part 2", 600), ("Credits", 30), ("Epilogue", 40))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(new ClassifiedChapterSegment(ContentSegmentType.Outro, 1200, 1230, ChapterSegmentMethod.Title));
    }

    [Fact]
    public void A_preview_title_alone_is_the_outro()
    {
        Classify(false, ("Part 1", 600), ("Part 2", 600), ("Next Episode Preview", 30))
            .Should()
            .ContainSingle(s => s.Type == ContentSegmentType.Outro && s.StartSeconds == 1200);
    }

    [Fact]
    public void An_ending_title_in_the_first_half_is_not_the_outro()
    {
        Classify(false, ("Credits", 30), ("Part 1", 600), ("Part 2", 600))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void A_recap_title_is_the_recap()
    {
        Classify(false, ("Previously On", 45), ("Opening", 30), ("Part 1", 1200))
            .Should()
            .Contain(new ClassifiedChapterSegment(ContentSegmentType.Recap, 0, 45, ChapterSegmentMethod.Title));
    }

    [Fact]
    public void A_last_chapter_is_never_an_intro()
    {
        Classify(false, ("Part 1", 1200), ("Opening", 90))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Generic_titles_on_a_non_anime_show_give_nothing()
    {
        Classify(false, ("Chapter 1", 90), ("Chapter 2", 600), ("Chapter 3", 600), ("Chapter 4", 100))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void By_length_the_opening_is_an_85_to_95_second_first_chapter()
    {
        Classify(true, ("Chapter 01", 90), ("Chapter 02", 600), ("Chapter 03", 600))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(new ClassifiedChapterSegment(ContentSegmentType.Intro, 0, 90, ChapterSegmentMethod.Length));
    }

    [Fact]
    public void By_length_a_chapter_before_the_opening_is_a_cold_open()
    {
        Classify(true, ("Chapter 01", 140), ("Chapter 02", 89), ("Chapter 03", 600), ("Chapter 04", 600))
            .Should()
            .ContainSingle()
            .Which.StartSeconds.Should()
            .Be(140);
    }

    [Fact]
    public void By_length_two_opening_sized_chapters_at_the_start_make_the_second_the_opening()
    {
        Classify(true, ("Chapter 01", 92), ("Chapter 02", 88), ("Chapter 03", 600), ("Chapter 04", 600))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(new ClassifiedChapterSegment(ContentSegmentType.Intro, 92, 180, ChapterSegmentMethod.Length));
    }

    [Fact]
    public void By_length_an_opening_sized_chapter_at_position_three_is_story()
    {
        Classify(true, ("Chapter 01", 120), ("Chapter 02", 300), ("Chapter 03", 90), ("Chapter 04", 600))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void By_length_the_ending_and_a_short_last_preview_are_one_outro()
    {
        Classify(true, ("Chapter 01", 90), ("Chapter 02", 600), ("Chapter 03", 600), ("Chapter 04", 90), ("Chapter 05", 14))
            .Should()
            .Contain(new ClassifiedChapterSegment(ContentSegmentType.Outro, 1290, 1394, ChapterSegmentMethod.Length));
    }

    [Theory]
    [InlineData(85)]
    [InlineData(125)]
    public void By_length_a_last_chapter_of_85_to_125_seconds_is_the_ending(double length)
    {
        Classify(true, ("Chapter 01", 300), ("Chapter 02", 600), ("Chapter 03", length))
            .Should()
            .ContainSingle(s => s.Type == ContentSegmentType.Outro && s.StartSeconds == 900);
    }

    [Theory]
    [InlineData(84)]
    [InlineData(126)]
    public void By_length_a_last_chapter_of_another_length_is_not_the_ending(double length)
    {
        Classify(true, ("Chapter 01", 300), ("Chapter 02", 600), ("Chapter 03", length))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void By_length_a_short_last_chapter_alone_is_not_reported()
    {
        Classify(true, ("Chapter 01", 300), ("Chapter 02", 600), ("Chapter 03", 600), ("Chapter 04", 10))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Generic_titles_include_the_start_chapter_the_scan_adds()
    {
        Classify(true, ("Start", 5), ("Chapter 1", 90), ("Chapter 2", 600), ("Chapter 3", 600))
            .Should()
            .ContainSingle(s => s.Type == ContentSegmentType.Intro && s.StartSeconds == 5);
    }

    [Fact]
    public void One_real_title_turns_the_length_rules_off()
    {
        Classify(true, ("Chapter 01", 90), ("Part A", 600), ("Chapter 03", 600), ("Chapter 04", 90))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Empty_chapters_are_ignored()
    {
        Classify(false, ("Part 1", 600), ("Credits", 30), ("Chapter 7", 0))
            .Should()
            .ContainSingle(s => s.Type == ContentSegmentType.Outro && s.EndSeconds == 630);
    }

    [Fact]
    public void Title_matches_have_full_confidence_and_length_matches_less()
    {
        new ClassifiedChapterSegment(ContentSegmentType.Intro, 0, 90, ChapterSegmentMethod.Title)
            .Confidence.Should()
            .Be(1.0);
        new ClassifiedChapterSegment(ContentSegmentType.Intro, 0, 90, ChapterSegmentMethod.Length)
            .Confidence.Should()
            .BeLessThan(1.0);
    }

    private IReadOnlyList<ClassifiedChapterSegment> Classify(
        bool anime,
        params (string Title, double Length)[] chapters
    )
    {
        List<ChapterSpan> spans = [];
        double start = 0;
        foreach ((string title, double length) in chapters)
        {
            spans.Add(new(title, start, start + length));
            start += length;
        }

        return _classifier.Classify(spans, anime);
    }
}
