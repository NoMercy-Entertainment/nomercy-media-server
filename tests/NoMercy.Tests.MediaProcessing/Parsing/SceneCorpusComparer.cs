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

using System.Globalization;
using System.Text.RegularExpressions;
using MovieFileLibrary;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>Compares what a parser said with what the corpus source asserts.</summary>
internal static partial class SceneCorpusComparer
{
    // Sonarr and Radarr test with stand-in names ("Series Title"); only their
    // numbers describe the parser's job there.
    [GeneratedRegex(
        @"\b(series|seriestitle|title|anime|show|movie)\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex PlaceholderWord();

    public static List<CorpusMismatch> Compare(
        SceneCorpusEntry entry,
        CorpusMode mode,
        MovieFile? parsed
    )
    {
        List<CorpusMismatch> mismatches = [];

        string expectedTitle = CorpusNameNormalizer.Title(entry.Title);
        if (expectedTitle.Length > 0 && !IsPlaceholder(entry, expectedTitle))
        {
            string ourTitle = CorpusNameNormalizer.Title(parsed?.Title);
            if (ourTitle != expectedTitle)
                mismatches.Add(new(entry, mode, "title", ourTitle, expectedTitle));
        }

        Check(mismatches, entry, mode, "year", entry.Year, ParseYear(parsed?.Year));
        Check(mismatches, entry, mode, "season", entry.Season, parsed?.Season);
        Check(mismatches, entry, mode, "episode", entry.Episode, parsed?.Episode);

        return mismatches;
    }

    private static void Check(
        List<CorpusMismatch> mismatches,
        SceneCorpusEntry entry,
        CorpusMode mode,
        string field,
        int? expected,
        int? actual
    )
    {
        if (expected.HasValue && expected != actual)
        {
            mismatches.Add(
                new(
                    entry,
                    mode,
                    field,
                    actual?.ToString(CultureInfo.InvariantCulture) ?? "null",
                    expected.Value.ToString(CultureInfo.InvariantCulture)
                )
            );
        }
    }

    private static bool IsPlaceholder(SceneCorpusEntry entry, string expectedTitle) =>
        entry.SourceRepo is "Sonarr/Sonarr" or "Radarr/Radarr"
        && PlaceholderWord().IsMatch(expectedTitle);

    private static int? ParseYear(string? year) =>
        int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
}
