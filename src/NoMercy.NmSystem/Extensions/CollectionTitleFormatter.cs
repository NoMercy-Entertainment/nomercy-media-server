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

namespace NoMercy.NmSystem.Extensions;

/// <summary>
/// Resolves the display name for a collection: the user's language, then English,
/// then the stored base title, with a trailing "Collection" word stripped.
/// </summary>
public static partial class CollectionTitleFormatter
{
    /// <summary>
    /// Picks the best available title (language, then English, then the base title)
    /// and strips a trailing "Collection" word from it.
    /// </summary>
    public static string ResolveDisplayTitle(
        string baseTitle,
        string? languageTitle,
        string? englishTitle
    )
    {
        string chosen = languageTitle.OrNull() ?? englishTitle.OrNull() ?? baseTitle;
        return StripCollectionSuffix(chosen);
    }

    /// <summary>
    /// Removes a trailing English word "Collection" (optionally preceded by
    /// whitespace), case-insensitive. Never reduces a name to an empty string.
    /// </summary>
    public static string StripCollectionSuffix(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return title;

        string stripped = TrailingCollectionWordRegex().Replace(title, string.Empty).TrimEnd();

        return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
    }

    [GeneratedRegex(@"\s*\bCollection\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingCollectionWordRegex();
}
