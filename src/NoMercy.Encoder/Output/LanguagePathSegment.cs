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

using System.Text;

namespace NoMercy.Encoder.Output;

/// <summary>
/// AUD-1174: a track tag never leaves the output folder. Every path that
/// carries a track language goes through here, so writers and readers agree.
/// </summary>
internal static class LanguagePathSegment
{
    private const string Fallback = "und";

    // The language comes raw from the source file, so only ASCII letters,
    // digits, '-' and '_' reach a path. The stored value stays raw.
    public static string From(string? language)
    {
        if (string.IsNullOrEmpty(language))
            return Fallback;

        StringBuilder builder = new(language.Length);
        foreach (char character in language)
        {
            if (
                character is '-' or '_'
                || (char.IsAscii(character) && char.IsLetterOrDigit(character))
            )
                builder.Append(character);
        }

        return builder.Length == 0 ? Fallback : builder.ToString();
    }
}
