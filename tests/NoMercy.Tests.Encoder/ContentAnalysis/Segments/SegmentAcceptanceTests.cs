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
/// The #598 acceptance: the results table and "no intro or credits start earlier
/// than the truth by more than 2 s", on the committed seasons. Hellsing S01 runs
/// from its real fingerprints with the server's own hop, so CI proves the
/// matching too; the others replay the prototype's recorded matches.
/// </summary>
[Trait("Category", "Unit")]
public class SegmentAcceptanceTests
{
    private readonly SeasonSegmentAnalyzer _analyzer = new();

    public static TheoryData<string> CommittedSeasons() => [.. ResearchCorpus.CommittedSeasons];

    [Theory]
    [MemberData(nameof(CommittedSeasons))]
    public void ResultsTable_Holds(string seasonId)
    {
        RecordedSeason season = ResearchCorpus.Committed(seasonId);

        IReadOnlyList<EpisodeSegments> result = Analyse(season, season.PrototypeMatches);

        AcceptanceChecks.Failures(season, result).Should().BeEmpty();
    }

    [Fact]
    public async Task HellsingS01_FromItsFingerprints_HoldsTheTable()
    {
        RecordedSeason season = ResearchCorpus.Committed("hellsing-s01");

        IReadOnlyList<PairMatches> matches = await _analyzer.MatchAsync(
            season.Signals,
            CancellationToken.None
        );
        IReadOnlyList<EpisodeSegments> result = Analyse(season, matches);

        AcceptanceChecks.Failures(season, result).Should().BeEmpty();
        // 1365-sample hop vs the prototype's 4096/3: the answer moves well under a second.
        PrototypeComparison
            .Differences(season, result, tolerance: 1.0, compareCandidates: false)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void HellsingS01_FromItsFingerprints_WithThePrototypesHop_IsThePrototypesAnswer()
    {
        RecordedSeason season = ResearchCorpus.Committed("hellsing-s01");

        List<PairMatches> matches = SeasonSegmentAnalyzer.MatchPairs(
            season.Signals,
            ChromaprintRaw.PrototypeSecondsPerHash,
            Environment.ProcessorCount,
            CancellationToken.None
        );
        IReadOnlyList<EpisodeSegments> result = Analyse(season, matches);

        PrototypeComparison
            .Differences(season, result, tolerance: 0.011, compareCandidates: true)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void HellsingS01_NextEpisodePreview_IsContentAfterTheOutro()
    {
        RecordedSeason season = ResearchCorpus.Committed("hellsing-s01");

        IReadOnlyList<EpisodeSegments> result = Analyse(season, season.PrototypeMatches);

        // E01-E12 end with a preview of the next episode after the ED: content,
        // never skipped. The finale has none and its credits run to the end.
        result
            .Take(12)
            .Should()
            .AllSatisfy(e =>
            {
                e.AfterOutro.Should().NotBeNull(e.Key);
                e.AfterOutro!.Value.Start.Should().Be(e.Outro!.End);
                e.ContentEnd.Seconds.Should().Be(e.AfterOutro.Value.End);
                e.CreditsStart.Should().Be(e.Outro.Start);
            });
        result[12].AfterOutro.Should().BeNull();
        result[12].ContentEnd.Seconds.Should().Be(result[12].Outro!.End);
    }

    [Fact]
    public void AmericanDadS02_SixOfEightCreditStarts_AreWithin1_2sOfTheChapter()
    {
        RecordedSeason season = ResearchCorpus.Committed("american-dad-s02");

        IReadOnlyList<EpisodeSegments> result = Analyse(season, season.PrototypeMatches);

        int close = 0;
        for (int i = 0; i < result.Count; i++)
        {
            double truth = ChapterScore
                .TruthOf(season.Id, season.Episodes[i].Chapters)
                .First(t => t.Kind == "outro")
                .Start;
            // 1.2 s as the issue rounds it (to 0.01 s).
            if (Math.Abs(result[i].CreditsStart!.Value - truth) < 1.205)
                close++;
        }

        close.Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void Krapopolis_OverlayCredits_GetAButtonButNeverAnAutoSkip()
    {
        RecordedSeason season = ResearchCorpus.Committed("krapopolis-s01");

        IReadOnlyList<EpisodeSegments> result = Analyse(season, season.PrototypeMatches);

        List<OutroBlock> outros = [.. result.Where(e => e.Outro is not null).Select(e => e.Outro!)];
        outros.Should().NotBeEmpty();
        outros.Should().AllSatisfy(o => o.ButtonOnly.Should().BeTrue("names over the last scene"));
    }

    [Fact]
    public void HellsingUltimate_CreditsOverATexturedBackground_ComeFromOcr()
    {
        RecordedSeason season = ResearchCorpus.Committed("hellsing-ultimate");

        IReadOnlyList<EpisodeSegments> result = Analyse(season, season.PrototypeMatches);

        result.Should().AllSatisfy(e => e.Outro!.FromText.Should().BeTrue());
    }

    [Fact]
    public void HellsingUltimate_WithoutOcr_DegradesToNoCredits_NeverToWrongOnes()
    {
        // OCR is optional on a server without the tesseract model. Without it the
        // textured-background credits are not found; nothing early is reported.
        RecordedSeason season = ResearchCorpus.Committed("hellsing-ultimate");
        List<EpisodeSignals> withoutOcr = [.. season.Signals.Select(s => s with { Ocr = null })];

        SeasonCandidates candidates = _analyzer.FindCandidates(withoutOcr, season.PrototypeMatches);
        IReadOnlyList<EpisodeSegments> result = _analyzer.Decide(candidates, season.Evidence());

        for (int i = 0; i < result.Count; i++)
        {
            double? withOcr = (double?)
                season.PrototypeAnalysis["episodes"]![i]!["final"]!["credits_start"];
            if (result[i].CreditsStart is { } start)
                start
                    .Should()
                    .BeGreaterThanOrEqualTo(withOcr!.Value - AcceptanceChecks.EarlyLimitSeconds);
        }
    }

    [Fact]
    public void Chernobyl_FinaleEpilogue_IsKept_ByTheSeasonCreditsLength()
    {
        RecordedSeason season = ResearchCorpus.Committed("chernobyl");

        IReadOnlyList<EpisodeSegments> result = Analyse(season, season.PrototypeMatches);

        EpisodeSegments finale = result.Single(e =>
            e.Key.EndsWith("S01E05", StringComparison.Ordinal)
        );
        finale.Outro!.Sources.Should().HaveFlag(OutroSources.SeasonLength);
        // 4.5 min of photos and text before the credits stay content.
        finale.CreditsStart.Should().BeApproximately(4177.52, 0.011);
    }

    [Fact]
    public async Task ShortSeasons_GetOnlyCreditsAndEndOfContent()
    {
        RecordedSeason season = ResearchCorpus.Committed("rick-and-morty-s01");
        List<EpisodeSignals> two = [.. season.Signals.Take(2)];

        IReadOnlyList<PairMatches> matches = await _analyzer.MatchAsync(
            two,
            CancellationToken.None
        );
        SeasonCandidates candidates = _analyzer.FindCandidates(two, matches);
        IReadOnlyList<EpisodeSegments> result = _analyzer.Decide(candidates, new SeasonEvidence(2));

        result
            .Should()
            .AllSatisfy(e =>
            {
                e.Intro.Should().BeNull("themes need partners");
                e.Candidates.Should().BeEmpty();
                e.CreditsStart.Should().NotBeNull("credits come from the picture alone");
            });
    }

    private IReadOnlyList<EpisodeSegments> Analyse(
        RecordedSeason season,
        IReadOnlyList<PairMatches> matches
    )
    {
        SeasonCandidates candidates = _analyzer.FindCandidates(season.Signals, matches);
        return _analyzer.Decide(candidates, season.Evidence());
    }
}
