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

using NoMercy.Encoder.Subtitles;

namespace NoMercy.Tests.Encoder.Subtitles;

public class WebVttSegmenterTests
{
    private readonly WebVttSegmenter _sut = new();

    // ----------------------------------------------------------------
    // Helper to build a minimal WebVTT string
    // ----------------------------------------------------------------

    private static string Vtt(params string[] cueBlocks)
    {
        string cues = string.Join("\n\n", cueBlocks);
        return string.IsNullOrEmpty(cues) ? "WEBVTT\n" : $"WEBVTT\n\n{cues}\n";
    }

    private static string Cue(string start, string end, string text) =>
        $"{start} --> {end}\n{text}";

    // ----------------------------------------------------------------
    // Single cue inside one segment
    // ----------------------------------------------------------------

    [Fact]
    public void SingleCue_InsideOneSegment_GoesOnlyToThatSegment()
    {
        // Cue covers 1s–3s; segment duration = 6s → falls entirely in segment 0.
        string vttContent = Vtt(Cue("00:00:01.000", "00:00:03.000", "Hello"));

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        segments.Should().HaveCount(1, "cue ends at 3 s, one 6-second segment covers it");
        segments[0].Content.Should().Contain("Hello");
        segments[0].Index.Should().Be(0);
    }

    [Fact]
    public void SingleCue_InsideSecondSegment_OnlyInSegmentOne()
    {
        // Cue at 7s–9s; segment duration = 6s → segment 1 covers [6,12).
        string vttContent = Vtt(Cue("00:00:07.000", "00:00:09.000", "World"));

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        segments.Should().HaveCount(2);
        segments[0].Content.Should().NotContain("World", "cue is in segment 1, not 0");
        segments[1].Content.Should().Contain("World");
    }

    // ----------------------------------------------------------------
    // Whole-track mode → one segment, not one file per time window
    // ----------------------------------------------------------------

    [Fact]
    public void SliceWhole_ManyCuesAcrossAFortyFiveMinuteTrack_IsOneSegmentWithEveryCue()
    {
        // 45 minutes of cues, one every 10 s. Per-window slicing at 6 s gave
        // ~457 files per language; the whole track must be one segment.
        string[] cues =
        [
            .. Enumerable
                .Range(0, 270)
                .Select(i =>
                    Cue(
                        TimeSpan.FromSeconds(i * 10).ToString(@"hh\:mm\:ss") + ".000",
                        TimeSpan.FromSeconds((i * 10) + 2).ToString(@"hh\:mm\:ss") + ".000",
                        $"line {i}"
                    )
                ),
        ];

        IReadOnlyList<WebVttSegment> segments = _sut.SliceWhole(Vtt(cues));

        segments.Should().HaveCount(1, "a subtitle track is one segment, not one file per window");
        segments[0].Index.Should().Be(0);
        segments[0].StartTime.Should().Be(TimeSpan.Zero);
        segments[0].EndTime.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds((269 * 10) + 2));
        segments[0].Content.Should().Contain("line 0").And.Contain("line 269");
        segments[0]
            .Content.Split("-->")
            .Length.Should()
            .Be(271, "every cue appears exactly once (no boundary duplication)");
    }

    [Fact]
    public void SliceWhole_NoCues_IsOneEmptySegment()
    {
        IReadOnlyList<WebVttSegment> segments = _sut.SliceWhole(Vtt());

        segments.Should().HaveCount(1);
        segments[0].Content.Should().StartWith("WEBVTT");
    }

    // ----------------------------------------------------------------
    // Cue straddling a boundary → duplicated into both segments
    // ----------------------------------------------------------------

    [Fact]
    public void CueStraddlingBoundary_DuplicatedIntoBothSegments()
    {
        // Cue at 5s–7s straddles the 6 s boundary → in segments 0 and 1.
        string vttContent = Vtt(Cue("00:00:05.000", "00:00:07.000", "Straddle"));

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        segments.Should().HaveCount(2);
        segments[0].Content.Should().Contain("Straddle", "cue overlaps segment 0 [0,6)");
        segments[1].Content.Should().Contain("Straddle", "cue overlaps segment 1 [6,12)");
    }

    // ----------------------------------------------------------------
    // Empty input file → single empty segment with WEBVTT header
    // ----------------------------------------------------------------

    [Fact]
    public void EmptyInputFile_SingleSegmentWithWebVttHeader()
    {
        string vttContent = "WEBVTT\n";

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        segments.Should().HaveCount(1);
        segments[0].Content.Should().StartWith("WEBVTT");
        segments[0].Content.Should().NotContain("-->", "empty input has no cue timestamps");
    }

    // ----------------------------------------------------------------
    // Input with X-TIMESTAMP-MAP → preserved per output segment
    // ----------------------------------------------------------------

    [Fact]
    public void InputWithTimestampMap_PreservedInOutput()
    {
        string vttContent =
            "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n"
            + Cue("00:00:01.000", "00:00:03.000", "Mapped");

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        segments.Should().HaveCount(1);
        segments[0].Content.Should().Contain("X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000");
    }

    [Fact]
    public void InputWithoutTimestampMap_StandardMapInjected()
    {
        string vttContent = Vtt(Cue("00:00:01.000", "00:00:02.000", "Plain"));

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        segments[0].Content.Should().Contain("X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000");
    }

    // ----------------------------------------------------------------
    // Segment index and time boundaries
    // ----------------------------------------------------------------

    [Fact]
    public void SegmentIndexAndBoundaries_AreCorrect()
    {
        string vttContent = Vtt([Cue("00:00:01.000", "00:00:02.000", "A"), Cue("00:00:07.000", "00:00:08.000", "B"), Cue("00:00:13.000", "00:00:14.000", "C")]
        );

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        segments.Should().HaveCount(3);
        segments[0].Index.Should().Be(0);
        segments[1].Index.Should().Be(1);
        segments[2].Index.Should().Be(2);

        segments[0].StartTime.Should().Be(TimeSpan.Zero);
        segments[0].EndTime.Should().Be(TimeSpan.FromSeconds(6));
        segments[1].StartTime.Should().Be(TimeSpan.FromSeconds(6));
        segments[2].StartTime.Should().Be(TimeSpan.FromSeconds(12));
    }

    // ── argument validation ────────────────────────────────────────────────

    [Fact]
    public void SliceContent_ZeroSegmentDuration_Throws()
    {
        // Zero would divide by zero downstream — guard the entry point.
        Action act = () => _sut.SliceContent(Vtt(), TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("segmentDuration");
    }

    [Fact]
    public void SliceContent_NegativeSegmentDuration_Throws()
    {
        Action act = () => _sut.SliceContent(Vtt(), TimeSpan.FromSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("segmentDuration");
    }

    // ── multiple cues in one segment ───────────────────────────────────────

    [Fact]
    public void MultipleCues_InSameSegment_BothPresent()
    {
        // Two cues fully inside segment 0 — both must land together.
        string vttContent = Vtt([Cue("00:00:01.000", "00:00:02.000", "First"), Cue("00:00:03.000", "00:00:04.000", "Second")]
        );

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        segments.Should().HaveCount(1);
        segments[0].Content.Should().Contain("First");
        segments[0].Content.Should().Contain("Second");
    }

    // ── hour-format timestamps ─────────────────────────────────────────────

    [Fact]
    public void HourFormatTimestamps_ParseCorrectly()
    {
        // VTT supports both HH:MM:SS.mmm and MM:SS.mmm. The regex accepts
        // optional hours — a long movie cue at 1h:00:01 must parse.
        string vttContent = Vtt(Cue("01:00:01.000", "01:00:03.000", "Late"));

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        // 1h = 3600s → segment 600 covers [3600, 3606); cue at 3601s lands there.
        segments.Should().HaveCountGreaterThan(599);
        segments[600].Content.Should().Contain("Late");
    }

    // ── cue exactly on boundary ────────────────────────────────────────────

    [Fact]
    public void CueEndingExactlyOnBoundary_OnlyInPriorSegment()
    {
        // Cue end == segment end → overlap predicate is `c.End > segStart`,
        // so cue does NOT carry into the next segment.
        string vttContent = Vtt(Cue("00:00:04.000", "00:00:06.000", "BoundaryEnd"));

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        segments.Should().HaveCount(1, "cue ends exactly at the 6 s boundary");
        segments[0].Content.Should().Contain("BoundaryEnd");
    }

    [Fact]
    public void CueStartingExactlyOnBoundary_OnlyInNextSegment()
    {
        // Cue starts at 6s exactly → only in segment 1, not segment 0.
        string vttContent = Vtt(Cue("00:00:06.000", "00:00:08.000", "BoundaryStart"));

        IReadOnlyList<WebVttSegment> segments = _sut.SliceContent(
            vttContent,
            TimeSpan.FromSeconds(6)
        );

        segments.Should().HaveCount(2);
        segments[0].Content.Should().NotContain("BoundaryStart");
        segments[1].Content.Should().Contain("BoundaryStart");
    }
}
