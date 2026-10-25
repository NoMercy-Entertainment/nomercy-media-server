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
using NoMercy.OpticalMedia.Sources;

namespace NoMercy.OpticalMedia.Rip;

/// <summary>
/// Builds the folder-relative output path for a ripped title in the shape
/// the media-library folder-watcher can match against TMDB:
/// <list type="bullet">
///   <item>movies → <c>{Title} ({Year})/{Title} ({Year}).mkv</c></item>
///   <item>TV/anime → <c>{Title} ({Year})/Season {SS}/{Title} S{SS}E{EE}.mkv</c></item>
///   <item>no metadata → <c>disc-rips/title_NN.mkv</c></item>
/// </list>
/// </summary>
public static partial class RipOutputPathHelper
{
    public static string Build(
        RipRequest request,
        string libraryType,
        int titleIndex,
        int batchIndex
    )
    {
        CustomMetadata? meta = request.Custom;
        if (meta is null || string.IsNullOrWhiteSpace(meta.Title))
            return $"disc-rips/title_{titleIndex:D2}.mkv";

        string safeTitle = SanitizeForPath(meta.Title);
        string yearSuffix = meta.Year is { } year ? $" ({year})" : "";
        string showRoot = $"{safeTitle}{yearSuffix}";

        switch (libraryType)
        {
            case "tv":
            case "anime":
                {
                    int season = meta.SeasonNumber ?? 1;
                    int episode = (meta.EpisodeStartNumber ?? 1) + batchIndex;
                    string seasonDir = $"Season {season:D2}";
                    string fileName = $"{safeTitle} S{season:D2}E{episode:D2}.mkv";
                    return $"{showRoot}/{seasonDir}/{fileName}";
                }
            case "movie":
                {
                    string suffix = batchIndex == 0 ? "" : $" - Disc {batchIndex + 1}";
                    return $"{showRoot}/{safeTitle}{yearSuffix}{suffix}.mkv";
                }
            default:
                return $"disc-rips/title_{titleIndex:D2}.mkv";
        }
    }

    private static string SanitizeForPath(string input)
    {
        string trimmed = InvalidFsChars().Replace(input, " ").Trim();
        return WhitespaceRun().Replace(trimmed, " ");
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvalidFsChars();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceRun();
}
