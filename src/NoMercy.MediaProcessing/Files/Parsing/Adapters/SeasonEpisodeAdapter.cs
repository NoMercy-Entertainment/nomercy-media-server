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
using NoMercy.NmSystem.Extensions;

namespace NoMercy.MediaProcessing.Files.Parsing.Adapters;

/// <summary>S##E## (or S##E####) anywhere in the file name (e.g.
/// "One.Piece.S01E1109.Title.mkv"); the show title is the text before the match.</summary>
public sealed partial class SeasonEpisodeAdapter : IFilenameParseAdapter
{
    public string Name => "season-episode";
    public int Order => 30;

    public MovieFile? TryParse(ParseContext context)
    {
        Match match = StringExtensions.MatchSeasonEpisode().Match(context.CleanedFileName);
        if (!match.Success)
            return null;

        string showTitle = context
            .CleanedFileName[..match.Index]
            .Replace('.', ' ')
            .Replace('_', ' ')
            .Trim();

        // Fansub releases label an episode twice — "Sousou no Frieren - 29 (S02E01)" — so the
        // text before the marker ends in the absolute number plus the marker's own opening
        // bracket. Both would be searched for verbatim, and no provider knows a show by that
        // name, so a whole season comes back unidentified.
        Match absolute = MatchTrailingAbsoluteEpisode().Match(showTitle);
        showTitle = MatchTrailingAbsoluteEpisode().Replace(showTitle, string.Empty);
        showTitle = showTitle.TrimEnd('-', '(', '[', '{', ' ').Trim();

        showTitle = showTitle.CleanSeriesTitle();

        if (string.IsNullOrWhiteSpace(showTitle) || showTitle.Length <= 1)
            showTitle = context.FolderTitle;

        // When the name carries both numberings, the absolute one wins and the season is left
        // for the folder to decide. Providers routinely model a long-running show as one
        // season, so its "S02E01" has no row to match while absolute 29 does — trusting the
        // marker there resolves to season 1 episode 1 and imports the wrong episode entirely.
        if (absolute.Success)
            return new(context.Title)
            {
                Title = showTitle,
                Episode = int.Parse(MatchNumbers().Match(absolute.Value).Value),
                IsSeries = true,
                IsSuccess = true,
            };

        return new(context.Title)
        {
            Title = showTitle,
            Season = int.Parse(match.Groups[1].Value),
            Episode = int.Parse(match.Groups[2].Value),
            IsSeries = true,
            IsSuccess = true,
        };
    }

    [GeneratedRegex(@"[0-9]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MatchNumbers();

    /// <summary>
    /// A trailing " - 29 (" style absolute episode label, including the opening bracket of
    /// the season marker that follows it. Requires the separator, so a show whose name simply
    /// ends in a number ("Mobile Suit Gundam 00") is left alone.
    /// </summary>
    [GeneratedRegex(@"\s-\s[0-9]{1,4}\s*[(\[{]?\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MatchTrailingAbsoluteEpisode();
}
