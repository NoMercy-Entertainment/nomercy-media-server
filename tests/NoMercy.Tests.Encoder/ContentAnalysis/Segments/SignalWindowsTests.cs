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

using NoMercy.Encoder.ContentAnalysis.Segments;
using NoMercy.Encoder.ContentAnalysis.Segments.Probes;
using NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments;

[Trait("Category", "Unit")]
public class SignalWindowsTests
{
    [Fact]
    public void Black_CoversEverySilenceAndTheLast480Seconds()
    {
        List<TimeRange> windows = SignalWindows.Black(
            1380.21,
            [new(1.44, 3.02), new(3.99, 5.13), new(81.07, 83.53), new(1377.2, 1380.21)]
        );

        windows.Should().HaveCount(3);
        windows[0].Start.Should().Be(0);
        windows[0].End.Should().BeApproximately(6.63, 1e-9);
        windows[1].Start.Should().BeApproximately(79.57, 1e-9);
        windows[2].Start.Should().BeApproximately(900.21, 1e-9);
        windows[2].End.Should().Be(1380.21);
    }

    [Fact]
    public void Black_RecordedWindows_AreTheResearchRunsWindows()
    {
        // The windows the research run measured Hellsing E01 in were built by
        // the same rule from the same silences.
        RecordedEpisode episode = ResearchCorpus.Committed("hellsing-s01").Episodes[0];

        List<TimeRange> windows = SignalWindows.Black(
            episode.Signals.DurationSeconds,
            episode.Signals.Silences
        );

        windows.Should().HaveCount(10);
        windows[^1].Start.Should().BeApproximately(900.21, 0.01);
    }

    [Fact]
    public void OcrStart_WithoutDarkCredits_IsTheLast420Seconds()
    {
        SignalWindows.OcrStart(3000, null).Should().Be(2580);
        SignalWindows.OcrStart(3000, new([], [new(1000, 1100)])).Should().Be(2580);
    }

    [Fact]
    public void OcrStart_WithDarkCredits_Is60SecondsBeforeTheirStart()
    {
        BlackRuns black = new([], [new(2900, 2930), new(2933, 2990)]);

        SignalWindows.OcrStart(3000, black).Should().Be(2840);
    }

    [Fact]
    public void OcrStart_NeverReachesBeforeTheLast420Seconds()
    {
        // A 70 min finale: a block starting 9 min before the end is still credits
        // (12 % of the duration), but OCR stays on the last 420 s.
        BlackRuns black = new([], [new(3660, 4150)]);

        SignalWindows.OcrStart(4200, black).Should().Be(3780);
    }
}
