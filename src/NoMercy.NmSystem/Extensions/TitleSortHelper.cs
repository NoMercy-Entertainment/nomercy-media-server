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

public static partial class TitleSortHelper
{
    [GeneratedRegex(@"^(The|An|A)\s+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingArticleRegex();

    [GeneratedRegex(@"[:]\s| and the ", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SubtitleSeparatorRegex();

    [GeneratedRegex(@"\.+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepeatedDotsRegex();

    private static string _parseTitleSort(string? value = null, DateTime? date = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        // Remove leading "The ", "An ", "A " (case-insensitive)
        value = LeadingArticleRegex().Replace(value, "");

        // Replace ": " and " and the " with the year if available
        if (date != null)
        {
            string year = date.Value.Year.ToString();
            value = SubtitleSeparatorRegex().Replace(value, $".{year}.");
        }

        // Replace multiple dots with a space (keeps readability)
        value = RepeatedDotsRegex().Replace(value, " ");

        // Sanitize file name to remove unwanted characters
        value = value.CleanFileName();

        return value.ToLower().Trim();
    }

    public static string TitleSort(this object self, int? parseYear)
    {
        return _parseTitleSort(
            self.ToString(),
            parseYear != null ? new DateTime(parseYear.Value, 1, 1) : null
        );
    }

    public static string TitleSort<T>(this T? self, DateTime? date = null)
    {
        return _parseTitleSort(self?.ToString(), date);
    }
}
