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

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>
/// Which episodes are matched against each other. Exhaustive matching grows
/// with the square of the season, so a long season (an anime with 50+
/// episodes in one season) matches each episode only against its
/// <see cref="SegmentRules.MaxPartners"/> nearest neighbours by episode order.
/// Themes stay where they are within a run of episodes, so near neighbours are
/// the partners that tell. Every corpus season has at most 13 episodes and
/// therefore still matches every pair.
/// </summary>
public static class SeasonPartners
{
    /// <summary>The pairs to match (X &lt; Y), in X-then-Y order.</summary>
    public static List<(int X, int Y)> Pairs(
        int episodes,
        int maxPartners = SegmentRules.MaxPartners
    )
    {
        HashSet<(int, int)> pairs = [];
        for (int i = 0; i < episodes; i++)
        {
            foreach (int j in Nearest(i, episodes, maxPartners))
                pairs.Add((Math.Min(i, j), Math.Max(i, j)));
        }

        return [.. pairs.OrderBy(p => p.Item1).ThenBy(p => p.Item2)];
    }

    /// <summary>How many partners each episode is matched against (its pairs).</summary>
    public static int[] Counts(int episodes, int maxPartners = SegmentRules.MaxPartners)
    {
        int[] counts = new int[episodes];
        foreach ((int x, int y) in Pairs(episodes, maxPartners))
        {
            counts[x]++;
            counts[y]++;
        }

        return counts;
    }

    /// <summary>The nearest other episodes by position, the earlier first on a tie.</summary>
    private static IEnumerable<int> Nearest(int episode, int episodes, int maxPartners) =>
        Enumerable
            .Range(0, episodes)
            .Where(j => j != episode)
            .OrderBy(j => Math.Abs(j - episode))
            .ThenBy(j => j)
            .Take(maxPartners);
}
