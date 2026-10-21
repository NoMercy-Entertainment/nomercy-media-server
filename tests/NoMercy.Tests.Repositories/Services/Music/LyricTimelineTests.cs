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
using NoMercy.Data.Services.Music;
using NoMercy.Database.Models.Music;

namespace NoMercy.Tests.Repositories.Services.Music;

[Trait("Category", "Unit")]
public class LyricTimelineTests
{
    private static Lyric Line(double total) =>
        new()
        {
            Text = "line",
            Time = new() { Total = total },
        };

    [Fact]
    public void ShiftBy_NoOffset_ReturnsTheSameLines()
    {
        Lyric[] lyrics = [Line(12.5)];

        LyricTimeline.ShiftBy(lyrics, null).Should().BeSameAs(lyrics);
        LyricTimeline.ShiftBy(lyrics, 0).Should().BeSameAs(lyrics);
    }

    [Fact]
    public void ShiftBy_PositiveOffset_ShiftsAndSplitsTheTime()
    {
        Lyric.LineTime time = LyricTimeline.ShiftBy([Line(59.5)], 1250)[0].Time;

        time.Total.Should().BeApproximately(60.75, 0.0001);
        time.Minutes.Should().Be(1);
        time.Seconds.Should().Be(0);
        time.Hundredths.Should().Be(75);
    }

    [Fact]
    public void ShiftBy_OffsetBeforeTheStart_ClampsToZero()
    {
        Lyric.LineTime time = LyricTimeline.ShiftBy([Line(1.0)], -3000)[0].Time;

        time.Total.Should().Be(0);
        time.Minutes.Should().Be(0);
        time.Seconds.Should().Be(0);
        time.Hundredths.Should().Be(0);
    }

    [Fact]
    public void ShiftBy_ShiftsTheWordsWithTheLine_AndClampsThemAtZero()
    {
        Lyric line = Line(1.0);
        line.Words =
        [
            new()
            {
                Text = "a",
                Start = 1.0,
                End = 1.4,
            },
            new()
            {
                Text = "b",
                Start = 1.5,
                End = 2.0,
            },
        ];

        Lyric shifted = LyricTimeline.ShiftBy([line], -1200)[0];

        shifted.Words.Should().HaveCount(2);
        shifted.Words![0].Start.Should().Be(0, "a word pushed before the start is clamped");
        shifted.Words[0].End.Should().BeApproximately(0.2, 0.0001);
        shifted.Words[1].Start.Should().BeApproximately(0.3, 0.0001);
        shifted.Words[1].End.Should().BeApproximately(0.8, 0.0001);
        line.Words[0].Start.Should().Be(1.0, "ShiftBy never mutates its input");
    }

    [Fact]
    public void ShiftBy_LeavesWordsNull_WhenTheLineHasNone()
    {
        LyricTimeline.ShiftBy([Line(1.0)], 500)[0].Words.Should().BeNull();
    }
}
