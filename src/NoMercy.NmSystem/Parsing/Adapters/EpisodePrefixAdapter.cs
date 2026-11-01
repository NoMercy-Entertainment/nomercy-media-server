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

/// <summary>S##E## at the start of the file name (e.g. "S01E01-some.title.mkv").
/// The show title is taken from the containing folder.</summary>
public sealed class EpisodePrefixAdapter : IFilenameParseAdapter
{
    public string Name => "episode-prefix";
    public int Order => 10;

    public MovieFile? TryParse(ParseContext context)
    {
        Match match = StringExtensions.MatchEpisodePrefix().Match(context.CleanedFileName);
        if (!match.Success)
            return null;

        return new(context.Title)
        {
            Title = context.FolderTitle.CleanReleaseTitle(),
            Season = int.Parse(match.Groups[1].Value),
            Episode = int.Parse(match.Groups[2].Value),
            IsSeries = true,
            IsSuccess = true,
        };
    }
}
