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

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// A corpus name turned into what a scan hands the parser: a file name, the
/// folder around it and the library it was found in.
/// </summary>
internal sealed record CorpusInput(string FileName, string? Directory, string LibraryType)
{
    private static readonly string[] KnownVideoExtensions =
    [
        ".mkv",
        ".mp4",
        ".avi",
        ".webm",
        ".flv",
        ".m4v",
        ".mov",
        ".wmv",
        ".ts",
        ".mpg",
        ".mpeg",
        ".ogm",
        ".divx",
    ];

    public string Path => Directory is null ? FileName : $"{Directory}/{FileName}";

    public static CorpusInput For(SceneCorpusEntry entry, CorpusMode mode)
    {
        string[] segments = entry.Name.Split('/', '\\');
        bool isPath = segments.Length > 1 && HasVideoExtension(segments[^1]);

        string leaf = isPath ? segments[^1] : entry.Name.Replace('/', ' ').Replace('\\', ' ');
        string stem = HasVideoExtension(leaf)
            ? System.IO.Path.GetFileNameWithoutExtension(leaf)
            : leaf;
        string fileName = stem + ".mkv";

        string? ownDirectory = isPath ? string.Join('/', segments[..^1]) : null;
        string? directory = mode switch
        {
            CorpusMode.BatchFolder => entry.Folder ?? $"Download/complete/{stem}",
            _ => ownDirectory,
        };

        return new(fileName, directory, LibraryTypeOf(entry));
    }

    private static bool HasVideoExtension(string name) =>
        KnownVideoExtensions.Contains(
            System.IO.Path.GetExtension(name),
            StringComparer.OrdinalIgnoreCase
        );

    private static string LibraryTypeOf(SceneCorpusEntry entry) =>
        entry.Kind switch
        {
            "movie" => "movie",
            "anime" => "anime",
            "tv" => "tv",
            _ => entry.Season.HasValue || entry.Episode.HasValue ? "tv" : "movie",
        };
}
