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

namespace NoMercy.MediaProcessing.Common;

/// <summary>
/// TMDB gives a title a list of origin countries; the Tv and Movie rows keep the
/// whole list in their single <c>OriginCountry</c> column, comma-joined
/// ("US,JP"). The anime check needs every country: a US/JP co-production is
/// anime, and keeping only the first one made it look American.
/// A row written before the whole list was kept holds one country, which reads
/// back as a one-item list; the next metadata refresh rewrites it in full.
/// </summary>
public static class OriginCountries
{
    private const char Separator = ',';

    /// <summary>The column value for TMDB's list, or null when the list is empty.</summary>
    public static string? Join(IEnumerable<string>? countries)
    {
        List<string> codes =
        [
            .. (countries ?? [])
                .Select(country => country.Trim())
                .Where(country => country.Length > 0),
        ];
        return codes.Count == 0 ? null : string.Join(Separator, codes);
    }

    /// <summary>The list a stored column value stands for, or null when nothing is stored.</summary>
    public static string[]? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        return stored.Split(
            Separator,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
        );
    }
}
