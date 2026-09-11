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

/// <summary>
/// How much of a track <see cref="IPluginAudioTools.SplitStemsAsync" /> ran
/// Spleeter over. A transition only ever touches the two ends of a track, so
/// separating four stems across an entire six-minute file to use twenty
/// seconds of it is wasted CPU a self-hosted owner is paying for on their own
/// machine.
/// </summary>
public enum PluginStemCoverage
{
    /// <summary>The whole track, start to end.</summary>
    Full = 0,

    /// <summary>
    /// The first 20 % of the track, the window a mix into the next track
    /// draws from. The exact bound is computed by the host from the track's
    /// duration, not by the caller.
    /// </summary>
    MixIn = 1,

    /// <summary>
    /// The last 25 % of the track, the window a mix out of it draws from. The
    /// exact bound is computed by the host from the track's duration, not by
    /// the caller.
    /// </summary>
    MixOut = 2,
}
