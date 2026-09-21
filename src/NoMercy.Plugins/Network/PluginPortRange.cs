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

namespace NoMercy.Plugins.Network;

/// <summary>
/// The port scope as a manifest writes it: one port, a range, or a comma list
/// of either. An absent scope is no port at all, never every port.
/// </summary>
public static class PluginPortRange
{
    public static bool Contains(string? declaredScope, int port)
    {
        if (string.IsNullOrWhiteSpace(declaredScope))
            return false;

        foreach (string part in declaredScope.Split(',', StringSplitOptions.TrimEntries))
        {
            string[] ends = part.Split('-', StringSplitOptions.TrimEntries);

            if (ends.Length == 1 && int.TryParse(ends[0], out int single) && single == port)
                return true;

            if (
                ends.Length == 2
                && int.TryParse(ends[0], out int low)
                && int.TryParse(ends[1], out int high)
                && port >= low
                && port <= high
            )
                return true;
        }

        return false;
    }

    /// <summary>Every declaration the manifest carries, read as one scope.</summary>
    public static bool Contains(IReadOnlyList<string>? declared, int port) =>
        declared is not null && declared.Any(scope => Contains(scope, port));
}
