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

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// Picks the one audio language a season is analysed in. Themes are matched by
/// sound, so every episode must use the same language; for anime that is the
/// original language (a dub's opening is often re-sung or cut).
/// </summary>
public static class AudioLanguagePicker
{
    // ISO 639-1 to the 639-2 codes the encoder writes into track names and rows.
    private static readonly Dictionary<string, string[]> Codes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["ja"] = ["jpn"],
        ["en"] = ["eng"],
        ["ko"] = ["kor"],
        ["zh"] = ["zho", "chi"],
        ["fr"] = ["fra", "fre"],
        ["de"] = ["deu", "ger"],
        ["es"] = ["spa"],
        ["it"] = ["ita"],
        ["pt"] = ["por"],
        ["ru"] = ["rus"],
        ["nl"] = ["nld", "dut"],
        ["sv"] = ["swe"],
        ["da"] = ["dan"],
        ["no"] = ["nor", "nob"],
        ["fi"] = ["fin"],
        ["pl"] = ["pol"],
        ["tr"] = ["tur"],
        ["ar"] = ["ara"],
        ["hi"] = ["hin"],
        ["th"] = ["tha"],
    };

    /// <summary>True when two language codes (639-1 or 639-2) name the same language.</summary>
    public static bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        return Codes.TryGetValue(a, out string[]? fromA)
                && fromA.Contains(b, StringComparer.OrdinalIgnoreCase)
            || Codes.TryGetValue(b, out string[]? fromB)
                && fromB.Contains(a, StringComparer.OrdinalIgnoreCase)
            || Codes.Any(c =>
                c.Value.Contains(a, StringComparer.OrdinalIgnoreCase)
                && c.Value.Contains(b, StringComparer.OrdinalIgnoreCase)
            );
    }

    /// <summary>
    /// The season's language: the original language when most episodes carry it,
    /// otherwise the language most episodes carry, otherwise null (each episode's
    /// first track).
    /// </summary>
    /// <param name="episodeLanguages">The audio languages of each episode.</param>
    public static string? Pick(
        string? originalLanguage,
        IReadOnlyList<IReadOnlyList<string>> episodeLanguages
    )
    {
        if (episodeLanguages.Count == 0)
            return null;

        int withOriginal = episodeLanguages.Count(l => l.Any(x => Same(x, originalLanguage)));
        if (originalLanguage is not null && withOriginal * 2 > episodeLanguages.Count)
        {
            return episodeLanguages
                .SelectMany(l => l)
                .FirstOrDefault(x => Same(x, originalLanguage));
        }

        return episodeLanguages
            .SelectMany(l => l.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
    }
}
