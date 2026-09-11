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
/// How eagerly a library's stems get produced and kept. Declared here so
/// that whatever schedules <see cref="IPluginAudioTools.SplitStemsAsync" />
/// across a library - a later contract, not this one - and a plugin reading
/// the result back through <see cref="IPluginMusicQuery" /> agree on one
/// vocabulary for it, rather than each inventing its own strings.
/// </summary>
public enum PluginStemPolicy
{
    /// <summary>
    /// Split only the mix-in and mix-out windows a transition needs, as
    /// <see cref="PluginStemCoverage.MixIn" /> and
    /// <see cref="PluginStemCoverage.MixOut" /> describe.
    /// </summary>
    Windows = 0,

    /// <summary>Split every track's full duration, as <see cref="PluginStemCoverage.Full" />.</summary>
    Full = 1,

    /// <summary>Split nothing ahead of time; split the first time a track is asked for.</summary>
    OnDemand = 2,
}
