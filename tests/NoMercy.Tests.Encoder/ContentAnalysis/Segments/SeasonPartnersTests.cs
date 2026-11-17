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

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments;

/// <summary>Matching cost stays bounded for long seasons.</summary>
[Trait("Category", "Unit")]
public class SeasonPartnersTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(13)]
    public void SeasonsOfUpTo13Episodes_MatchEveryPair(int episodes)
    {
        SeasonPartners.Pairs(episodes).Should().HaveCount(episodes * (episodes - 1) / 2);
        SeasonPartners.Counts(episodes).Should().AllSatisfy(c => c.Should().Be(episodes - 1));
    }

    [Fact]
    public void ALongSeason_MatchesEachEpisodeAgainstItsNearestNeighboursOnly()
    {
        const int episodes = 100;

        List<(int X, int Y)> pairs = SeasonPartners.Pairs(episodes);
        int[] counts = SeasonPartners.Counts(episodes);

        // 4950 pairs exhaustively; with 12 neighbours each about a quarter of that.
        pairs.Count.Should().BeLessThanOrEqualTo(episodes * SegmentRules.MaxPartners / 2 + SegmentRules.MaxPartners * SegmentRules.MaxPartners);
        counts.Should().AllSatisfy(c => c.Should().BeInRange(SegmentRules.MaxPartners, 2 * SegmentRules.MaxPartners));
        pairs.Should().Contain((49, 55)).And.NotContain((49, 56), "six on either side in the middle of a season");
        pairs.Should().Contain((0, 12), "the first episode takes its twelve partners from after it");
    }

    [Fact]
    public async Task ALongSeason_IsMatchedOnlyOnItsBoundedPairs()
    {
        List<EpisodeSignals> season =
        [
            .. Enumerable.Range(0, 40).Select(i => new EpisodeSignals(
                $"E{i:00}",
                60,
                [new FingerprintPart(0, [.. Enumerable.Range(0, 200).Select(k => (uint)(k * 2654435761u + i))])],
                [],
                null,
                null
            )),
        ];

        IReadOnlyList<PairMatches> matches = await new SeasonSegmentAnalyzer().MatchAsync(season, CancellationToken.None);

        matches.Should().HaveCount(SeasonPartners.Pairs(40).Count);
        matches.Count.Should().BeLessThan(40 * 39 / 2);
    }

    [Fact]
    public async Task Matching_WaitsWhilePlaybackIsActive()
    {
        int done = 0;
        bool playing = true;
        Task run = LowPriorityParallel.ForAsync(4, 2, _ => Interlocked.Increment(ref done), () => Volatile.Read(ref playing), CancellationToken.None);

        await Task.Delay(1500);
        Volatile.Read(ref done).Should().Be(0, "nothing is matched while media plays");

        Volatile.Write(ref playing, false);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        done.Should().Be(4);
    }

    [Fact]
    public async Task Matching_StopsOnCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> run = () => LowPriorityParallel.ForAsync(4, 2, _ => { }, null, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }
}
