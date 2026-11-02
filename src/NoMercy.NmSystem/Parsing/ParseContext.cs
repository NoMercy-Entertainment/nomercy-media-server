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

namespace NoMercy.MediaProcessing.Files.Parsing;

/// <summary>
/// Immutable input to the filename parse pipeline. Carries the raw filename plus
/// a few pre-derived values (bracket-stripped name, folder-derived title) so every
/// adapter works from the same normalized starting point.
/// </summary>
public sealed class ParseContext
{
    /// <summary>File name including extension, e.g. "One.Piece.S01E1109.mkv".</summary>
    public required string FileNameWithExtension { get; init; }

    /// <summary>Directory the file lives in, used for folder-derived titles.</summary>
    public string? DirectoryName { get; init; }

    /// <summary>The caller's working title (full path with brackets removed).</summary>
    public required string Title { get; init; }

    /// <summary>File name without extension, with [bracketed] segments removed.</summary>
    public string CleanedFileName { get; init; } = string.Empty;

    /// <summary>Title derived from the containing folder, pre-computed by the caller.</summary>
    public string FolderTitle { get; init; } = string.Empty;

    /// <summary>Library type (movie/tv/anime/music); lets adapters be series-type aware.</summary>
    public string LibraryType { get; init; } = string.Empty;
}
