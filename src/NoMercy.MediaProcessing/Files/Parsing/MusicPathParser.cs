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

namespace NoMercy.MediaProcessing.Files.Parsing;

/// <summary>
/// Parses an audio library directory path laid out as
/// <c>LibraryFolder / (Letter | [Type]) / Artist / ([Year] | [Singles]) Album</c>
/// into its artist / year / album parts. Extracted verbatim from the previously
/// inlined (and duplicated) FileListService logic so the single regex is named,
/// reusable and covered by its own corpus. Behaviour is intentionally unchanged.
/// </summary>
public static partial class MusicPathParser
{
    [GeneratedRegex(
        @"(?<library_folder>.+?)[\\\/]((?<letter>.{1})?|\[(?<type>.+?)\])[\\\/](?<artist>.+?)?[\\\/]?(\[(?<year>[0-9]{4})\]|\[(?<releaseType>Singles)\])\s?(?<album>.*)?",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    public static partial Regex MatchAlbumPath();

    [GeneratedRegex(@"\[[0-9]{4}\]\s?", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex MatchYearTag();

    /// <summary>Parsed album metadata. Year is 0 when absent.</summary>
    public readonly record struct MusicAlbumInfo(
        int Year,
        string AlbumName,
        string? Artist,
        string? ReleaseType
    );

    /// <summary>
    /// Parses <paramref name="directoryPath"/>. When the path does not match the
    /// expected layout, the album name falls back to <paramref name="folderName"/>
    /// with any leading [year] tag stripped (mirroring the original behaviour).
    /// </summary>
    public static MusicAlbumInfo Parse(string directoryPath, string folderName)
    {
        Match match = MatchAlbumPath().Match(directoryPath);

        int year = match.Groups["year"].Success ? int.Parse(match.Groups["year"].Value) : 0;

        string albumName = match.Groups["album"].Success
            ? match.Groups["album"].Value
            : MatchYearTag().Replace(folderName, string.Empty);

        string? artist = match.Groups["artist"].Success ? match.Groups["artist"].Value : null;
        string? releaseType = match.Groups["releaseType"].Success
            ? match.Groups["releaseType"].Value
            : null;

        return new(year, albumName, artist, releaseType);
    }
}
