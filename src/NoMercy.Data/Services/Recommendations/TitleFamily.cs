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

namespace NoMercy.Data.Services.Recommendations;

/// <summary>
/// Groups titles into franchise families: two titles belong together when they share
/// the first 60% of the shorter one ("Tom and Jerry: The Movie", "Tom and Jerry: Willy Wonka").
/// </summary>
public static class TitleFamily
{
    /// <summary>
    /// The family <paramref name="title"/> belongs to among <paramref name="families"/>,
    /// adding it as a new family when none matches.
    /// </summary>
    public static string Assign(string title, List<string> families)
    {
        foreach (string family in families)
        {
            int prefixLen = CommonPrefixLength(title, family);
            int minLen = Math.Min(title.Length, family.Length);
            if (minLen > 0 && prefixLen >= minLen * 0.6)
                return family;
        }

        families.Add(title);
        return title;
    }

    private static int CommonPrefixLength(string a, string b)
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            if (char.ToLowerInvariant(a[i]) != char.ToLowerInvariant(b[i]))
                return i;
        }
        return len;
    }
}
