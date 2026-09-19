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
    // 11.1 added IPluginMusicQuery.GetFailedDjAnalysisAsync and PluginTrackDjFailure. Additive
    // for every plugin already loading; a plugin that targets 11.1 needs a host that has the
    // member, which is what the minor ceiling in IsCompatible refuses on an 11.0 server -
    // instead of a MissingMethodException on the plugin's first call.
    public static Version Current { get; } = new(11, 1);

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
