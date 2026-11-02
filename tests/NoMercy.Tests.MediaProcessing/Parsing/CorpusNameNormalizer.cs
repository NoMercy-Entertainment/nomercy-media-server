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

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// Two titles are the same when only case, dots, dashes, underscores, brackets,
/// accents, apostrophes or a leading "The" tell them apart.
/// </summary>
internal static partial class CorpusNameNormalizer
{
    [GeneratedRegex(
        @"[^\p{L}\p{N}]+",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"^the\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingThe();

    public static string Title(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        string lowered = RemoveAccents(title).ToLowerInvariant().Replace("'", "").Replace("’", "");
        string spaced = NonAlphanumeric().Replace(lowered, " ").Trim();

        return LeadingThe().Replace(spaced, string.Empty);
    }

    private static string RemoveAccents(string text)
    {
        StringBuilder builder = new();
        foreach (char letter in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(letter) != UnicodeCategory.NonSpacingMark)
                builder.Append(letter);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
