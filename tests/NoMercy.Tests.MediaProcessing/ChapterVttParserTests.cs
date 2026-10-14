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
using NoMercy.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing;

[Trait("Category", "Unit")]
public class ChapterVttParserTests
{
    // The exact shape ChapterWriter produces: WEBVTT header, then blank-line
    // separated blocks of `Chapter N` / timing / title.
    private const string RealChaptersVtt =
        "WEBVTT\n\n"
        + "Chapter 1\n00:00:00.000 --> 00:01:14.833\nScene 01\n\n"
        + "Chapter 2\n00:01:14.833 --> 00:02:17.417\nIntro\n\n"
        + "Chapter 3\n00:02:17.417 --> 00:26:32.417\nScene 03\n\n"
        + "Chapter 4\n00:26:32.417 --> 00:28:46.084\nCredits\n";

    [Fact]
    public void Parses_real_chapter_vtt_into_titled_cues_in_milliseconds()
    {
        List<IChapter> chapters = FileManager.ParseChaptersVtt(RealChaptersVtt);

        chapters.Should().HaveCount(4);

        chapters[0]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Id = 0,
                    StartTime = 0,
                    EndTime = 74833,
                    Title = "Scene 01",
                }
            );
        chapters[1]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Id = 1,
                    StartTime = 74833,
                    EndTime = 137417,
                    Title = "Intro",
                }
            );
        chapters[2]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Id = 2,
                    StartTime = 137417,
                    EndTime = 1592417,
                    Title = "Scene 03",
                }
            );
        chapters[3]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Id = 3,
                    StartTime = 1592417,
                    EndTime = 1726084,
                    Title = "Credits",
                }
            );
    }

    [Fact]
    public void Does_not_emit_header_blank_or_id_lines_as_chapters()
    {
        List<IChapter> chapters = FileManager.ParseChaptersVtt(RealChaptersVtt);

        chapters
            .Select(chapter => chapter.Title)
            .Should()
            .NotContain(["WEBVTT", "", "Chapter 1", "00:00:00.000 --> 00:01:14.833"]);
    }

    [Fact]
    public void Handles_crlf_line_endings()
    {
        string crlf = RealChaptersVtt.Replace("\n", "\r\n");

        List<IChapter> chapters = FileManager.ParseChaptersVtt(crlf);

        chapters.Should().HaveCount(4);
        chapters[0].Title.Should().Be("Scene 01");
        chapters[0].StartTime.Should().Be(0);
    }

    [Fact]
    public void Skips_cues_with_unparseable_timing()
    {
        const string malformed =
            "WEBVTT\n\n"
            + "Chapter 1\nnot-a-timestamp --> also-bad\nBroken\n\n"
            + "Chapter 2\n00:00:10.000 --> 00:00:20.000\nGood\n";

        List<IChapter> chapters = FileManager.ParseChaptersVtt(malformed);

        // The malformed cue is skipped; the surviving "Good" cue starts at 10s,
        // so a synthetic "Start" chapter is prepended to cover 0 -> 10s.
        chapters.Should().HaveCount(2);
        chapters[0].Title.Should().Be("Start");
        chapters[0].StartTime.Should().Be(0);
        chapters[0].EndTime.Should().Be(10000);
        chapters[1].Title.Should().Be("Good");
        chapters[1].StartTime.Should().Be(10000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("WEBVTT")]
    [InlineData("WEBVTT\n\n")]
    public void Returns_empty_for_headerless_or_cueless_input(string text)
    {
        FileManager.ParseChaptersVtt(text).Should().BeEmpty();
    }

    [Fact]
    public void Prepends_a_start_chapter_when_a_lone_late_cue_leaves_a_gap_from_zero()
    {
        // The exact What.If S01E01 shape: a single "Credits" cue starting at
        // 29:58, nothing before it. Without a leading chapter the player shows
        // one marker floating near the end. Synthesize "Start" from 0.
        const string lateOnly =
            "WEBVTT\n\n" + "Chapter 1\n00:29:58.833 --> 00:31:15.936\nCredits\n";

        List<IChapter> chapters = FileManager.ParseChaptersVtt(lateOnly);

        chapters.Should().HaveCount(2);
        chapters[0]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Id = 0,
                    StartTime = 0,
                    EndTime = 1798833,
                    Title = "Start",
                }
            );
        chapters[1]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Id = 1,
                    StartTime = 1798833,
                    EndTime = 1875936,
                    Title = "Credits",
                }
            );
    }

    [Fact]
    public void Reads_older_files_whose_cues_run_together_without_blank_lines()
    {
        // Older chapter files have one blank line, after the header. Split on blank
        // lines they read as one cue, so an episode was stored with a lone 47-second
        // "Intro" and the apps took the rest of the episode for the intro.
        const string runTogether =
            "WEBVTT\n\n"
            + "Chapter 1\n00:00:00 --> 00:00:47\nIntro\n"
            + "Chapter 2\n00:00:47 --> 00:02:16\nOP\n"
            + "Chapter 3\n00:02:16 --> 00:22:42\nPart A\n"
            + "Chapter 4\n00:22:42 --> 00:24:28\nED\n";

        List<IChapter> chapters = FileManager.ParseChaptersVtt(runTogether);

        chapters.Select(chapter => chapter.Title).Should().Equal("Intro", "OP", "Part A", "ED");
        chapters.Select(chapter => chapter.StartTime).Should().Equal(0, 47000, 136000, 1362000);
        chapters.Select(chapter => chapter.EndTime).Should().Equal(47000, 136000, 1362000, 1468000);
    }

    [Fact]
    public void Does_not_prepend_when_the_first_chapter_already_starts_at_zero()
    {
        // RealChaptersVtt's first cue starts at 00:00:00.000, so the timeline is
        // already covered — no synthetic "Start" chapter, ids unchanged.
        List<IChapter> chapters = FileManager.ParseChaptersVtt(RealChaptersVtt);

        chapters.Should().HaveCount(4);
        chapters[0].Title.Should().Be("Scene 01");
        chapters[0].StartTime.Should().Be(0);
    }
}
