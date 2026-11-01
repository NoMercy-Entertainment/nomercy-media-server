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

/// <summary>Cross-format numbering: "1x05", "12x08", "1×05" (SeasonxEpisode).
/// Resolution tokens like 1920x1080 are excluded because the digits before the
/// separator are preceded by another digit.</summary>
public sealed class CrossFormatAdapter : IFilenameParseAdapter
{
    public string Name => "cross-format";
    public int Order => 25;

    public MovieFile? TryParse(ParseContext context)
    {
        Match match = StringExtensions.MatchCrossFormatEpisode().Match(context.CleanedFileName);
        if (!match.Success)
            return null;

        // A cross-format match at the very start is usually the title itself —
        // "4x4" is a film and a series, "3x3" is a fansub group.
        //
        // A file named for nothing but its marker ("1x02 Ep Name.mkv") is
        // ordinary in a series library though, with the show's name on the
        // folder; that is the same shape as "S01E02 Ep Name.mkv", which the
        // prefix adapter has always accepted. What separates the two is the
        // padding: a marker written without a title to introduce it carries the
        // convention that makes it readable as one, and a single-digit "4x4"
        // never does.
        if (
            match.Index == 0
            && (
                match.Groups[2].Length < 2
                || (
                    context.LibraryType != MediaTypes.AnimeMediaType
                    && context.LibraryType != MediaTypes.TvMediaType
                )
            )
        )
            return null;

        string showTitle = context
            .CleanedFileName[..match.Index]
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
            Season = int.Parse(match.Groups[1].Value),
            Episode = int.Parse(match.Groups[2].Value),
            IsSeries = true,
            IsSuccess = true,
        };
    }
}
