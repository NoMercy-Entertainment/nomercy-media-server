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
/// The whole 13-season, 93-episode corpus from the research folder, every
/// fingerprint matched in C#. Runs only when
/// <see cref="ResearchCorpus.ResearchEnvironmentVariable"/> points at a
/// nomercy-segment-research checkout; skipped otherwise.
/// </summary>
[Trait("Category", "Integration")]
public class ResearchCorpusTests
{
    private readonly SeasonSegmentAnalyzer _analyzer = new();

    public static TheoryData<string> AllSeasons() => [.. ResearchCorpus.AllSeasons];

    [SkippableTheory]
    [MemberData(nameof(AllSeasons))]
    public void Season_FromRecordedMatches_IsThePrototypesAnswer(string seasonId)
    {
        RecordedSeason season = LoadOrSkip(seasonId);

        SeasonCandidates candidates = _analyzer.FindCandidates(season.Signals, season.PrototypeMatches);
        IReadOnlyList<EpisodeSegments> result = _analyzer.Decide(candidates, season.Evidence());

        PrototypeComparison
            .Differences(season, result, tolerance: 0.011, compareCandidates: true)
            .Should()
            .BeEmpty();
    }

    [SkippableTheory]
    [MemberData(nameof(AllSeasons))]
    public void Season_FromItsFingerprints_HoldsTheResultsTable(string seasonId)
    {
        RecordedSeason season = LoadOrSkip(seasonId);

        IReadOnlyList<PairMatches> matches = _analyzer.Match(season.Signals, CancellationToken.None);
        SeasonCandidates candidates = _analyzer.FindCandidates(season.Signals, matches);
        IReadOnlyList<EpisodeSegments> result = _analyzer.Decide(candidates, season.Evidence());

        AcceptanceChecks.Failures(season, result).Should().BeEmpty();
    }

    [SkippableTheory]
    [MemberData(nameof(AllSeasons))]
    public void Season_FromItsFingerprints_WithThePrototypesHop_GivesThePrototypesMatchesAndAnswer(
        string seasonId
    )
    {
        RecordedSeason season = LoadOrSkip(seasonId);

        List<PairMatches> matches = SeasonSegmentAnalyzer.MatchPairs(
            season.Signals,
            ChromaprintRaw.PrototypeSecondsPerHash,
            Environment.ProcessorCount,
            CancellationToken.None
        );
        SeasonCandidates candidates = _analyzer.FindCandidates(season.Signals, matches);
        IReadOnlyList<EpisodeSegments> result = _analyzer.Decide(candidates, season.Evidence());

        PrototypeComparison.MatchDifferences(season, matches).Should().BeEmpty();
        // Candidates are compared in PrototypeFidelityTests from the recorded
        // matches: the recording rounded every time to 1 ms, which moves a bin
        // edge now and then (a coverage of 0.667 vs 0.657), never an answer.
        PrototypeComparison
            .Differences(season, result, tolerance: 0.011, compareCandidates: false)
            .Should()
            .BeEmpty();
    }

    [SkippableFact]
    public void HashCount_AgainstDuration_FitsTheChromaprintHop()
    {
        // #345 measured over every whole-track print (22 min to 70 min episodes).
        Skip.If(ResearchCorpus.ResearchRoot is null, "research folder not configured");
        List<(double Hashes, double Samples)> points = [];
        foreach (string id in ResearchCorpus.AllSeasons)
        {
            foreach (RecordedEpisode episode in LoadOrSkip(id).Episodes)
            {
                if (episode.Signals.Fingerprint is [{ OffsetSeconds: 0 } part])
                    points.Add((part.Hashes.Length, episode.Signals.DurationSeconds * ChromaprintRaw.SampleRate));
            }
        }

        double meanX = points.Average(p => p.Hashes);
        double meanY = points.Average(p => p.Samples);
        double slope =
            points.Sum(p => (p.Hashes - meanX) * (p.Samples - meanY))
            / points.Sum(p => (p.Hashes - meanX) * (p.Hashes - meanX));

        points.Should().HaveCountGreaterThan(80);
        slope.Should().BeApproximately(ChromaprintRaw.HopSamples, 0.1);
    }

    private static RecordedSeason LoadOrSkip(string seasonId)
    {
        string? root = ResearchCorpus.ResearchRoot;
        Skip.If(root is null, $"set {ResearchCorpus.ResearchEnvironmentVariable} to the nomercy-segment-research folder");
        return ResearchCorpus.Load(Path.Combine(root!, seasonId));
    }
}
