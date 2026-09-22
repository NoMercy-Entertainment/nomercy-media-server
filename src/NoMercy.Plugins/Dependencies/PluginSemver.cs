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

namespace NoMercy.PluginSdk.Dependencies;

/// <summary>
/// Whether an installed version satisfies the range a manifest asks for.
/// <para>
/// The whole grammar the manifest field allows: comparisons joined by spaces,
/// all of which must hold. Small on purpose, because a range nobody can read
/// out loud is a range an author gets wrong and a server enforces anyway.
/// </para>
/// </summary>
public static class PluginSemver
{
    public static bool Satisfies(Version installed, string range)
    {
        string[] clauses = range.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // An empty range is every version. An author who wrote nothing did not
        // mean "no version will do".
        return clauses.Length == 0 || clauses.All(clause => Holds(installed, clause));
    }

    private static bool Holds(Version installed, string clause)
    {
        (string op, string rest) = Split(clause);

        if (!Version.TryParse(rest, out Version? wanted))
            return false;

        int order = installed.CompareTo(wanted);

        return op switch
        {
            ">=" => order >= 0,
            "<=" => order <= 0,
            ">" => order > 0,
            "<" => order < 0,
            _ => order == 0,
        };
    }

    private static (string Operator, string Version) Split(string clause)
    {
        foreach (string op in (string[])[">=", "<=", ">", "<", "="])
        {
            if (clause.StartsWith(op, StringComparison.Ordinal))
                return (op, clause[op.Length..]);
        }

        return ("=", clause);
    }
}
