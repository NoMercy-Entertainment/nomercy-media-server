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

namespace NoMercy.Plugins.Abstractions;

public static class PluginAbi
{
    public static Version Current { get; } = new(11, 0);

    public static bool IsCompatible(string? targetAbi)
    {
        if (string.IsNullOrWhiteSpace(targetAbi))
        {
            return true;
        }

        if (!Version.TryParse(targetAbi, out Version? requested))
        {
            return false;
        }

        if (requested.Major == Current.Major - 1)
        {
            return true;
        }

        return requested.Major == Current.Major && requested.Minor <= Current.Minor;
    }
}
