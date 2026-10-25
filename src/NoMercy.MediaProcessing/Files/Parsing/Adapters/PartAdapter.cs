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
using MovieFileLibrary;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.MediaProcessing.Files.Parsing.Adapters;

/// <summary>
/// A file numbered by part rather than by episode: "Show Name - Part 3",
/// "Cleopatra_Part_1_of_2", "Show.Name.Part.IV", "Show.Name.Part.1.and.Part.2".
/// <para>
/// The part number is the episode, and the "of 2" that follows it is the total
/// — not a second number. Reading the total was landing both halves of a
/// two-part release on the same episode: Cleopatra part one and part two were
/// both episode 2, so one silently replaced the other.
/// </para>
/// <para>
/// Ordered after every explicit marker and after the specials adapter, so
/// "S01E03" and "Movie Part 1" are already claimed. It declines whenever a
/// standalone number FOLLOWS the part marker, because that number is the
/// episode and the part is a cour or a disc: "[Group] Show Part 2 - 05" is
/// episode five, not episode two.
/// </para>
/// </summary>
public sealed partial class PartAdapter : IFilenameParseAdapter
{
    public string Name => "part";
    public int Order => 45;

    /// <summary>
    /// The part marker, its optional "of N" total, and any "and Part N"
    /// continuation — all consumed together so nothing downstream reads one of
    /// their numbers as the episode.
    /// <para>The boundary is any letter in any script, not just ASCII. "part" is
    /// an ordinary word inside a Japanese episode title — "「風邪の日と、ねこねこ
    /// part3」" — and a marker glued to the end of a word is not a marker.</para>
    /// </summary>
    [GeneratedRegex(
        matchTimeoutMilliseconds: 1000,
        options: RegexOptions.IgnoreCase,
        pattern: @"(?<![\p{L}\p{N}])(?:part|pt)[\s._-]*(?<num>[0-9]{1,3}|[IVXLC]{1,6})"
            + @"(?:[\s._-]*of[\s._-]*[0-9]{1,3})?"
            + @"(?:[\s._-]*(?:and|&|to)[\s._-]*(?:(?:part|pt)[\s._-]*)?[0-9]{1,3})*"
            + @"(?![\p{L}\p{N}])"
    )]
    private static partial Regex PartMarker();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[0-9]{1,4}(?![A-Za-z0-9])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StandaloneNumber();

    public MovieFile? TryParse(ParseContext context)
    {
        if (
            context.LibraryType != MediaTypes.AnimeMediaType
            && context.LibraryType != MediaTypes.TvMediaType
        )
            return null;

        Match match = PartMarker().Match(context.CleanedFileName);
        if (!match.Success)
            return null;

        string afterMarker = context.CleanedFileName[(match.Index + match.Length)..];
        if (StandaloneNumber().IsMatch(afterMarker))
            return null;

        int? episode = ParsePartNumber(match.Groups["num"].Value);
        if (episode is null)
            return null;

        string showTitle = context
            .CleanedFileName[..match.Index]
            .Replace('.', ' ')
            .Replace('_', ' ')
            .TrimEnd('-', ' ')
            .Trim()
            .CleanSeriesTitle();

        if (string.IsNullOrWhiteSpace(showTitle) || showTitle.Length <= 1)
            showTitle = context.FolderTitle;

        return new(context.Title)
        {
            Title = showTitle,
            Episode = episode,
            IsSeries = true,
            IsSuccess = true,
        };
    }

    private static int? ParsePartNumber(string raw)
    {
        if (int.TryParse(raw, out int arabic))
            return arabic > 0 ? arabic : null;

        return RomanNumeral.TryParse(raw);
    }
}
