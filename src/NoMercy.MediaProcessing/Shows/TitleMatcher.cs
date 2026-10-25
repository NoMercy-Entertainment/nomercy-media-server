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

using System.Text.RegularExpressions;

namespace NoMercy.MediaProcessing.Shows;

/// <summary>
/// Word-SET title comparison shared by every anime-classification provider
/// call. Ported from the Kitsu-era matcher: candidate title fields differ
/// per provider (AniList: romaji/english/native/synonyms; Jikan: a titles
/// array; Kitsu, historically: fixed en/en_us/en_jp/ja_jp/th_th fields),
/// but all of them fail an exact or prefix compare on real titles —
/// reordering, disambiguating year suffixes, curly vs straight
/// apostrophes, and inconsistent dash/colon subtitle separators are all
/// real, reproduced cases. See TitleMatcherTests for each one.
/// </summary>
public static partial class TitleMatcher
{
    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CollapseWhitespace();

    private static string Normalize(string title) =>
        CollapseWhitespace()
            .Replace(
                title
                    .Replace('-', ' ')
                    .Replace('–', ' ')
                    .Replace('—', ' ')
                    .Replace('~', ' ')
                    .Replace('～', ' ')
                    .Replace('.', ' ')
                    .Replace(':', ' ')
                    .Replace("&", " and ")
                    .Replace("★", " ")
                    .Replace("☆", " ")
                    .Replace("♀", " ")
                    .Replace("♂", " ")
                    .Replace("’", string.Empty)
                    .Replace("'", string.Empty),
                " "
            )
            .Trim();

    private static string[] WordsFor(string title) =>
        Normalize(title).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // TMDB prefixes English titles with an article that AniList and Jikan
    // routinely drop ("The Piano Forest" vs "Piano Forest"), and requiring every
    // search word to appear then rejects the correct hit. Dropping them from the
    // search side only still requires every meaningful word to match.
    private static readonly HashSet<string> IgnorableWords = ["the", "a", "an"];

    public static bool Matches(string searchTitle, IEnumerable<string?> candidateTitles)
    {
        string[] searchWords =
        [
            .. WordsFor(searchTitle).Where(word => !IgnorableWords.Contains(word)),
        ];
        if (searchWords.Length == 0)
            return false;

        foreach (string? candidate in candidateTitles)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;

            HashSet<string> candidateWords = WordsFor(candidate).ToHashSet();
            if (searchWords.All(candidateWords.Contains))
                return true;
        }

        return false;
    }
}
