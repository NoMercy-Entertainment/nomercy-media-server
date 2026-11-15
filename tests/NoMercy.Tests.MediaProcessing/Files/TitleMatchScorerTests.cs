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
using NoMercy.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing.Files;

public class TitleMatchScorerTests
{
    private sealed record Candidate(int Id, string Title, DateTime? Date);

    private static Candidate? Pick(string parsedTitle, string? parsedYear, params Candidate[] all) =>
        TitleMatchScorer.PickBest(all, c => c.Title, c => c.Date, parsedTitle, parsedYear);

    [Fact]
    public void An_exact_title_is_chosen_over_an_earlier_longer_title()
    {
        Candidate? best = Pick(
            "Harbor Lights",
            null,
            new Candidate(1, "Harbor Lights Behind the Scenes", null),
            new Candidate(2, "Harbor Lights", null)
        );

        best.Should().NotBeNull();
        best!.Id.Should().Be(2);
    }

    [Fact]
    public void The_year_breaks_a_tie_between_equal_titles()
    {
        Candidate? best = Pick(
            "Harbor Lights",
            "2026",
            new Candidate(1, "Harbor Lights", new DateTime(2001, 1, 1)),
            new Candidate(2, "Harbor Lights", new DateTime(2026, 5, 1))
        );

        best!.Id.Should().Be(2);
    }

    [Fact]
    public void A_best_candidate_below_the_confidence_floor_is_refused()
    {
        Pick("Harbor Lights", null, new Candidate(1, "Zebra Quarry", null)).Should().BeNull();
    }

    [Fact]
    public void No_candidates_give_no_match()
    {
        Pick("Harbor Lights", null).Should().BeNull();
    }
}
