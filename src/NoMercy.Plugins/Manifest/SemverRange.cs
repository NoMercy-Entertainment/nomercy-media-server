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

namespace NoMercy.Plugins.Manifest;

/// <summary>
/// The five range operators the marketplace issues, and nothing else. A range
/// this cannot read refuses, so an unreadable range never reads as satisfied.
/// </summary>
public static class SemverRange
{
    public static bool Satisfies(string version, string range)
    {
        if (!Version.TryParse(version, out Version? actual))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*")
        {
            return true;
        }

        string[] alternatives = range.Split("||", StringSplitOptions.RemoveEmptyEntries);

        foreach (string alternative in alternatives)
        {
            if (SatisfiesAll(actual, alternative))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SatisfiesAll(Version actual, string alternative)
    {
        string[] comparators = alternative.Split(
            [' ', ',', '\t'],
            StringSplitOptions.RemoveEmptyEntries
        );

        if (comparators.Length == 0)
        {
            return false;
        }

        foreach (string comparator in comparators)
        {
            if (!SatisfiesOne(actual, comparator))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SatisfiesOne(Version actual, string comparator)
    {
        string trimmed = comparator.Trim();
        string op = "=";

        foreach (string candidate in new[] { ">=", "<=", ">", "<", "=" })
        {
            if (trimmed.StartsWith(candidate, StringComparison.Ordinal))
            {
                op = candidate;
                trimmed = trimmed[candidate.Length..].Trim();
                break;
            }
        }

        if (!Version.TryParse(trimmed, out Version? bound))
        {
            return false;
        }

        int comparison = actual.CompareTo(bound);

        return op switch
        {
            ">=" => comparison >= 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            "<" => comparison < 0,
            _ => comparison == 0,
        };
    }
}
