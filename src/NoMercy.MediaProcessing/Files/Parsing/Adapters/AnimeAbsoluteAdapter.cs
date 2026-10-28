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
/// Anime absolute numbering, e.g. "[Group] One Piece - 1109 [1080p]" -> episode 1109.
/// Active only for anime/TV libraries so it can never claim a movie's year as an
/// episode. Years and resolution tokens are excluded, and it is ordered after the
/// SxxExx / cross-format adapters so explicit numbering always wins. Season is left
/// unset so the absolute-episode resolver downstream is allowed to run.
/// </summary>
public sealed partial class AnimeAbsoluteAdapter : IFilenameParseAdapter
{
    public string Name => "anime-absolute";
    public int Order => 50;

    [GeneratedRegex(@"(?<![A-Za-z0-9])([0-9]{1,4})(?![A-Za-z0-9])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StandaloneNumber();

    /// <summary>
    /// A hyphen-joined run of numbers — the "03-04" of "Show Name - 03-04 - Ep
    /// Name", the "001-013" of a batch. The run names a span, and a span starts
    /// at its first number.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])([0-9]{1,4})(?:-[0-9]{1,4})+(?![A-Za-z0-9])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberRun();

    public MovieFile? TryParse(ParseContext context)
    {
        if (
            context.LibraryType != MediaTypes.AnimeMediaType
            && context.LibraryType != MediaTypes.TvMediaType
        )
            return null;

        MatchCollection matches = StandaloneNumber().Matches(context.CleanedFileName);
        Match? episode = null;
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            if (LooksLikeYear(matches[i].Groups[1].Value))
                continue;
            episode = matches[i];
            break;
        }

        if (episode is null)
            return null;

        // The last number wins because the trailing one is the episode and the
        // leading ones are the show's name ("Mob Psycho 100 - 07"). A range
        // breaks that: both halves are trailing, so "Show Name - 03-04 - Ep
        // Name" took 04. The file holds episodes three AND four, so it was
        // filed as the second one — episode three then reads as missing and
        // episode four as a duplicate of a file that is not it.
        foreach (Match run in NumberRun().Matches(context.CleanedFileName))
        {
            if (episode.Index < run.Index || episode.Index >= run.Index + run.Length)
                continue;

            episode = run;
            break;
        }

        string showTitle = context
            .CleanedFileName[..episode.Index]
            .Replace('.', ' ')
            .Replace('_', ' ')
            .TrimEnd('-', ' ')
            .Trim();

        showTitle = showTitle.CleanSeriesTitle();

        if (string.IsNullOrWhiteSpace(showTitle) || showTitle.Length <= 1)
            showTitle = context.FolderTitle;

        return new(context.Title)
        {
            Title = showTitle,
            Episode = int.Parse(episode.Groups[1].Value),
            IsSeries = true,
            IsSuccess = true,
        };
    }

    private static bool LooksLikeYear(string number) =>
        number.Length == 4
        && (number.StartsWith("18") || number.StartsWith("19") || number.StartsWith("20"));
}
