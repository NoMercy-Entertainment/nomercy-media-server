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
using NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments;

/// <summary>
/// The C# stages 3–8 against the research prototype that produced the #598
/// results table: fed the same recorded inputs (and the prototype's recorded
/// matches), they must give the prototype's answer for every episode, every
/// candidate and every piece of evidence, to 0.01 s.
/// </summary>
[Trait("Category", "Unit")]
public class PrototypeFidelityTests
{
    private const double Tolerance = 0.011;

    private readonly SeasonSegmentAnalyzer _analyzer = new();

    public static TheoryData<string> CommittedSeasons() => [.. ResearchCorpus.CommittedSeasons];

    [Theory]
    [MemberData(nameof(CommittedSeasons))]
    public void PureStages_OnRecordedInputs_GiveThePrototypesAnswer(string seasonId)
    {
        RecordedSeason season = ResearchCorpus.Committed(seasonId);

        SeasonCandidates candidates = _analyzer.FindCandidates(
            season.Signals,
            season.PrototypeMatches
        );
        IReadOnlyList<EpisodeSegments> result = _analyzer.Decide(candidates, season.Evidence());

        PrototypeComparison
            .Differences(season, result, Tolerance, compareCandidates: true)
            .Should()
            .BeEmpty();
    }
}
