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

namespace NoMercy.Networking.Http;

/// <summary>
/// Parses the operator's trusted-proxy setting: single addresses and CIDR ranges,
/// separated by commas, semicolons or whitespace. A bad entry is skipped and
/// reported, never thrown, so a typo cannot stop the server from starting (#125).
/// </summary>
public static class TrustedProxyList
{
    public const string ConfigKey = "security.trusted_proxies";

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    public static IReadOnlyList<IpRange> Parse(string? raw, out List<string> rejected)
    {
        rejected = [];
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        List<IpRange> ranges = [];
        foreach (
            string entry in raw.Split(
                Separators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (IpRange.TryParse(entry, out IpRange range))
                ranges.Add(range);
            else
                rejected.Add(entry);
        }

        return ranges;
    }
}
