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
/// A name that says which season it belongs to and never says which episode:
/// "Show Name Season 2", "Show.Name.S02.Source.Quality.Etc-Group".
/// <para>
/// The season number was the only number in the name, so it was read as the
/// episode and the file took season one's second slot — on top of whatever real
/// episode already lived there. Season two became episode two of season one.
/// </para>
/// <para>
/// Fires only when removing the season marker leaves no number behind. "Show S01
/// - 05" still has a five, so that five is the episode and this declines; the
/// season marker there is context, not the whole answer.
/// </para>
/// </summary>
public sealed partial class SeasonPackAdapter : IFilenameParseAdapter
{
    public string Name => "season-pack";
    public int Order => 44;

    /// <summary>"Season 2", "Series 2", the localised spellings, or a bare "S02".</summary>
    [GeneratedRegex(
        matchTimeoutMilliseconds: 1000,
        options: RegexOptions.IgnoreCase,
        pattern: @"(?<![\p{L}\p{N}])(?:(?:season|series|saison|staffel|temporada|stagione)[\s._-]*0*(?<season>[0-9]{1,2})"
            + @"|S(?<season2>[0-9]{1,2}))(?![\p{L}\p{N}])"
    )]
    private static partial Regex SeasonMarker();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[0-9]{1,4}(?![A-Za-z0-9])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StandaloneNumber();

    public MovieFile? TryParse(ParseContext context)
    {
        if (
            context.LibraryType != MediaTypes.AnimeMediaType
            && context.LibraryType != MediaTypes.TvMediaType
        )
            return null;

        Match match = SeasonMarker().Match(context.CleanedFileName);
        if (!match.Success)
            return null;

        string withoutMarker =
            context.CleanedFileName[..match.Index]
            + " "
            + context.CleanedFileName[(match.Index + match.Length)..];

        if (StandaloneNumber().IsMatch(withoutMarker))
            return null;

        Group season = match.Groups["season"].Success
            ? match.Groups["season"]
            : match.Groups["season2"];

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
            Season = int.Parse(season.Value),
            IsSeries = true,
            IsSuccess = true,
        };
    }
}
