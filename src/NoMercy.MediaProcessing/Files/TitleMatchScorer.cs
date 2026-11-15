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
using NoMercy.NmSystem.Extensions;

namespace NoMercy.MediaProcessing.Files;

/// <summary>
/// The one rule every identification path (folder scan, file watcher, Add content)
/// uses to choose among TMDB search results.
/// </summary>
public static class TitleMatchScorer
{
    public const double MinConfidence = 50.0;

    private const double YearBonus = 25.0;

    public static double Score(
        string candidateTitle,
        DateTime? candidateDate,
        string parsedTitle,
        string? parsedYear
    )
    {
        double score = FuzzyMatcher.MatchPercentage(candidateTitle, parsedTitle);
        if (int.TryParse(parsedYear, out int year) && candidateDate?.Year == year)
            score += YearBonus;
        return score;
    }

    /// <summary>
    /// The best scored candidate, or null when its title does not look like the
    /// parsed one closely enough to trust.
    /// </summary>
    public static T? PickBest<T>(
        IEnumerable<T> candidates,
        Func<T, string> title,
        Func<T, string?> originalTitle,
        Func<T, DateTime?> date,
        string parsedTitle,
        string? parsedYear
    )
        where T : class
    {
        T? best = candidates.MaxBy(candidate =>
            Math.Max(
                Score(title(candidate), date(candidate), parsedTitle, parsedYear),
                Score(originalTitle(candidate) ?? string.Empty, date(candidate), parsedTitle, parsedYear)
            )
        );

        if (best is null)
            return null;

        double similarity = Math.Max(
            FuzzyMatcher.MatchPercentage(title(best), parsedTitle),
            FuzzyMatcher.MatchPercentage(originalTitle(best) ?? string.Empty, parsedTitle)
        );
        return similarity < MinConfidence ? null : best;
    }
}
