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

namespace NoMercy.PluginSdk.Abstractions;

public static class PluginAbi
{
    // 12.1 added IPluginMusicQuery.GetFailedDjAnalysisAsync, PluginTrackDjFailure and the
    // PluginTopics.MusicAnalysisCompleted topic. Additive for every plugin already loading.
    public static Version Current { get; } = new(12, 2);

    /// <summary>
    /// The oldest major this server can load at all.
    /// <para>
    /// Usually one behind <see cref="Current" />: a major removes members, and
    /// a plugin that never called them keeps working, so the grace costs
    /// nothing and saves every author a forced rebuild.
    /// </para>
    /// <para>
    /// Twelve is not that kind of major. The SDK assembly and every namespace
    /// in it were renamed, so a plugin built against eleven resolves no type at
    /// all. Granting it grace would install a plugin that then fails at load
    /// with a missing-type error naming nothing its author recognises, instead
    /// of a refusal that says rebuild.
    /// </para>
    /// </summary>
    public static Version Oldest { get; } = new(12, 0);

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

        if (requested.Major < Oldest.Major)
        {
            return false;
        }

        if (requested.Major < Current.Major)
        {
            return true;
        }

        return requested.Major == Current.Major && requested.Minor <= Current.Minor;
    }
}
