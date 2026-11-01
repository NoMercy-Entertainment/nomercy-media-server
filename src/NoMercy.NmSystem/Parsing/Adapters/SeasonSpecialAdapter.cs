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
/// Season-scoped specials written as <c>S##S##</c> — "season 1's special 3" —
/// which is how Judas, Erai-raws and most BD batch releases number the extras
/// that ship alongside a season. Maps to season 0, keeping the special's own
/// number as the episode.
/// <para>
/// Restricted to anime/TV libraries and ordered directly after the S##E##
/// matcher, whose marker it deliberately cannot match: without it these names
/// reached the movie detector, came back with neither season nor episode, and
/// the caller's last-resort "first number in the title" rule then read the
/// SEASON digits as the episode — so every special in a season arrived as
/// S##E01, each one overwriting the last.
/// </para>
/// </summary>
public sealed partial class SeasonSpecialAdapter : IFilenameParseAdapter
{
    public string Name => "season-special";

    // Ahead of every episode matcher. Both forms it claims carry a marker those
    // matchers have no rule for — a season-scoped special, or an episode with a
    // half — and each of them reads the part it recognises and drops the rest,
    // so "S01E21.5" arrived as plain episode 21 and took the real one's place.
    // Nothing here can match a plain SxxExx, so looking first costs them nothing.
    public int Order => 5;

    // SP / OVA / OAD precede the bare S in the alternation so "S07SP01" is read
    // as season 7's SP 1 rather than backtracking through a season-then-S match.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])S(?<season>[0-9]{1,2})[\s\.\-_]*(?:SP|OVA|OAD|S)(?<special>[0-9]{1,3})(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SeasonSpecialMarker();

    /// <summary>
    /// A recap or side-story that airs between two episodes is numbered with a
    /// half — "NANA - S01E21.5", "S02E05.5 [SP]". It was read as plain episode 21
    /// and took the real episode's place.
    /// <para>
    /// The fraction is one digit that ends the token: no further digit, letter or
    /// dot may follow. Scene names separate with dots, so both
    /// "S05E14.1080p.BluRay" and an episode titled with a number
    /// ("The.Punisher.S01E01.3.AM") would otherwise read as a half-episode.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])S(?<season>[0-9]{1,2})E(?<special>[0-9]{1,3})\.[0-9](?![0-9A-Za-z.])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FractionalEpisode();

    public MovieFile? TryParse(ParseContext context)
    {
        if (
            context.LibraryType != MediaTypes.AnimeMediaType
            && context.LibraryType != MediaTypes.TvMediaType
        )
            return null;

        Match match = SeasonSpecialMarker().Match(context.CleanedFileName);
        if (!match.Success)
            match = FractionalEpisode().Match(context.CleanedFileName);
        if (!match.Success)
            return null;

        string title = context
            .CleanedFileName[..match.Index]
            .Replace('.', ' ')
            .Replace('_', ' ')
            .TrimEnd('-', ' ')
            .Trim()
            .CleanSeriesTitle();

        if (string.IsNullOrWhiteSpace(title) || title.Length <= 1)
            title = context.FolderTitle;

        if (string.IsNullOrWhiteSpace(title) || title.Length <= 1)
            return null;

        return new(context.Title)
        {
            Title = title,
            Season = 0,
            Episode = int.Parse(match.Groups["special"].Value),
            IsSeries = true,
            IsSuccess = true,
        };
    }
}
